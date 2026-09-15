namespace DotnetVendor;

using System.Diagnostics;

public static class VendorCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null) return 1;

        // In --update mode, inherit exclusion settings from the previous manifest
        // when the user didn't pass them explicitly on the CLI. Explicit CLI flags
        // always override — no implicit merging.
        var manifestPath = Path.Combine(options.OutputDir, "vendor-manifest.json");
        VendorManifest? previousManifest = null;
        if (options.Update)
            previousManifest = await ManifestWriter.ReadAsync(manifestPath);

        var effectiveExcludePrefixes = options.ExcludePrefixes;
        var effectiveNoDefaultExcludes = options.NoDefaultExcludes;
        var inheritedExcludes = false;
        var inheritedNoDefaultExcludes = false;
        if (options.Update && previousManifest is not null)
        {
            if (!options.ExcludePrefixesExplicit && previousManifest.ExcludePrefixes.Count > 0)
            {
                effectiveExcludePrefixes = previousManifest.ExcludePrefixes;
                inheritedExcludes = true;
            }
            if (!options.NoDefaultExcludesExplicit && previousManifest.NoDefaultExcludes)
            {
                effectiveNoDefaultExcludes = true;
                inheritedNoDefaultExcludes = true;
            }
        }

        Console.WriteLine($"dotnet-vendor: vendoring source for project at {options.ProjectDir}");
        Console.WriteLine($"  output directory : {options.OutputDir}");
        Console.WriteLine($"  fallback decompile: {options.FallbackDecompile}");
        Console.WriteLine($"  max parallelism   : {options.Parallelism}");
        if (options.Update)
            Console.WriteLine("  mode              : update (stale packages will be refreshed)");
        if (effectiveExcludePrefixes.Count > 0)
            Console.WriteLine($"  exclude prefixes  : {string.Join(", ", effectiveExcludePrefixes)}{(inheritedExcludes ? " (inherited from manifest)" : "")}");
        if (effectiveNoDefaultExcludes)
            Console.WriteLine($"  no-default-excludes: true{(inheritedNoDefaultExcludes ? " (inherited from manifest)" : "")}");
        if (options.DryRun)
            Console.WriteLine("  *** DRY RUN — no files will be written ***");
        Console.WriteLine();

        // Step 1: Resolve the packages used by the project
        var packages = await PackageResolver.ResolveAsync(options.ProjectDir, effectiveExcludePrefixes, effectiveNoDefaultExcludes);
        if (packages.Count == 0)
        {
            Console.Error.WriteLine("No packages found. Did you run 'dotnet restore' first?");
            return 1;
        }

        Console.WriteLine($"Found {packages.Count} package(s) to vendor.\n");

        // Step 2: For each package, discover the repository URL + commit
        var repoInfos = await RepositoryDiscoverer.DiscoverAllAsync(packages, options.Parallelism);

        // Step 3: Clone / decompile
        if (!options.DryRun)
            Directory.CreateDirectory(options.OutputDir);

        // In --update mode, use the previous manifest (already loaded above) to
        // detect which on-disk packages are stale vs. still current. Outside
        // --update mode we keep the existing behavior of skipping any directory
        // that exists.
        var previousByPackage = options.Update && previousManifest is not null
            ? previousManifest.Packages.ToDictionary(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, VendorManifestEntry>(StringComparer.OrdinalIgnoreCase);

        int cloned = 0, decompiled = 0, skipped = 0, refreshed = 0;

        // Group by repo URL so we only clone each repo once
        var byRepo = repoInfos
            .Where(r => r.RepoUrl is not null)
            .GroupBy(r => NormalizeRepoUrl(r.RepoUrl!))
            .ToList();

        // Packages with no known repo URL → decompile or skip
        var noRepo = repoInfos.Where(r => r.RepoUrl is null).ToList();

        using var progress = options.DryRun ? null : new ProgressBar(byRepo.Count + noRepo.Count);

        foreach (var group in byRepo)
        {
            var repoUrl = group.Key;
            // Prefer the entry that has a commit hash; pick first otherwise
            var best = group.FirstOrDefault(r => r.Commit is not null) ?? group.First();
            var destDir = Path.Combine(options.OutputDir, SanitizeDirName(best.PackageId));

            if (Directory.Exists(destDir))
            {
                if (!options.Update || IsStillCurrent(best, previousByPackage))
                {
                    if (!options.DryRun)
                    {
                        progress?.Clear();
                        Console.WriteLine($"  [skip] {best.PackageId} — {(options.Update ? "up to date" : "already exists")} at {destDir}");
                        skipped++;
                        progress?.Tick();
                    }
                    continue;
                }

                // --update + stale: delete and re-fetch.
                if (!options.DryRun)
                {
                    progress?.Clear();
                    Console.WriteLine($"  [stale] {best.PackageId} — refreshing {destDir}");
                    await RunProcess("rm", $"-rf \"{destDir}\"", timeoutSeconds: 30);
                    // Also remove any sibling .repo-ref markers that pointed here.
                    foreach (var other in group.Where(r => r.PackageId != best.PackageId))
                    {
                        var refPath = Path.Combine(options.OutputDir, SanitizeDirName(other.PackageId)) + ".repo-ref";
                        if (File.Exists(refPath)) File.Delete(refPath);
                    }
                }
                refreshed++;
            }

            var ghRepo = TryParseGitHubRepo(best.RepoUrl!);
            var method = ghRepo is not null ? "tar" : "git";

            if (options.DryRun)
            {
                Console.WriteLine($"  [{method}]  {best.PackageId} <- {repoUrl}{(best.Commit is not null ? $" @ {best.Commit[..Math.Min(12, best.Commit.Length)]}" : "")}");
                cloned++;
                foreach (var other in group.Where(r => r.PackageId != best.PackageId))
                    Console.WriteLine($"  [ref]  {other.PackageId} -> {best.PackageId}");
                continue;
            }

            bool ok;
            progress?.Clear();
            if (ghRepo is not null)
            {
                Console.Write($"  [tar]  {best.PackageId} <- {repoUrl}");
                ok = await DownloadGitHubTarball(ghRepo.Value.Owner, ghRepo.Value.Repo, best.Commit, destDir);
                if (!ok)
                {
                    // Tarball failed, fall back to git clone
                    Console.Write(" -> [git]");
                    ok = await GitClone(best.RepoUrl!, best.Commit, destDir);
                }
            }
            else
            {
                Console.Write($"  [git]  {best.PackageId} <- {repoUrl}");
                ok = await GitClone(best.RepoUrl!, best.Commit, destDir);
            }
            if (ok)
            {
                Console.WriteLine(" ✓");
                cloned++;

                // If multiple packages share the same repo, symlink/note them
                foreach (var other in group.Where(r => r.PackageId != best.PackageId))
                {
                    var linkPath = Path.Combine(options.OutputDir, SanitizeDirName(other.PackageId));
                    if (!Directory.Exists(linkPath))
                    {
                        File.WriteAllText(linkPath + ".repo-ref",
                            $"# This package lives in the same repository as {best.PackageId}\n" +
                            $"# See: {destDir}\n");
                    }
                }
            }
            else
            {
                Console.WriteLine(" ✗ (clone failed)");
                // Try decompile fallback
                if (options.FallbackDecompile)
                {
                    foreach (var pkg in group)
                    {
                        var decDir = Path.Combine(options.OutputDir, SanitizeDirName(pkg.PackageId));
                        if (await Decompiler.TryDecompile(pkg.PackageId, pkg.Version, decDir))
                            decompiled++;
                        else
                            skipped++;
                    }
                }
                else skipped += group.Count();
            }
            progress?.Tick();
        }

        foreach (var pkg in noRepo)
        {
            var destDir = Path.Combine(options.OutputDir, SanitizeDirName(pkg.PackageId));
            if (Directory.Exists(destDir))
            {
                if (!options.Update || IsStillCurrent(pkg, previousByPackage))
                {
                    if (!options.DryRun)
                    {
                        skipped++;
                        progress?.Tick();
                    }
                    continue;
                }
                if (!options.DryRun)
                {
                    progress?.Clear();
                    Console.WriteLine($"  [stale] {pkg.PackageId} — refreshing {destDir}");
                    await RunProcess("rm", $"-rf \"{destDir}\"", timeoutSeconds: 30);
                }
                refreshed++;
            }

            if (options.FallbackDecompile)
            {
                if (options.DryRun)
                {
                    Console.WriteLine($"  [dec]  {pkg.PackageId} (no repo URL)");
                    decompiled++;
                }
                else
                {
                    progress?.Clear();
                    Console.Write($"  [dec]  {pkg.PackageId} (no repo URL)");
                    if (await Decompiler.TryDecompile(pkg.PackageId, pkg.Version, destDir))
                    {
                        Console.WriteLine(" ✓");
                        decompiled++;
                    }
                    else
                    {
                        Console.WriteLine(" ✗");
                        skipped++;
                    }
                    progress?.Tick();
                }
            }
            else
            {
                progress?.Clear();
                Console.WriteLine($"  [skip] {pkg.PackageId} — no repository URL found");
                skipped++;
                progress?.Tick();
            }
        }

        progress?.Dispose();
        Console.WriteLine();
        Console.WriteLine($"{(options.DryRun ? "Dry run complete" : "Done")}. cloned={cloned}, decompiled={decompiled}, refreshed={refreshed}, skipped={skipped}");

        if (!options.DryRun)
        {
            await ManifestWriter.WriteAsync(manifestPath, repoInfos, effectiveExcludePrefixes, effectiveNoDefaultExcludes);
            Console.WriteLine($"Manifest written to {manifestPath}");
        }

        return 0;
    }

    static bool IsStillCurrent(RepoInfo current, Dictionary<string, VendorManifestEntry> previous)
    {
        if (!previous.TryGetValue(current.PackageId, out var prev)) return false;

        // If we know the commit on both sides, the commit is authoritative.
        if (current.Commit is not null && prev.Commit is not null)
            return string.Equals(current.Commit, prev.Commit, StringComparison.OrdinalIgnoreCase);

        // Otherwise fall back to version equality (e.g. decompile path, or
        // discovery sources that didn't yield a commit).
        return string.Equals(current.Version, prev.Version, StringComparison.OrdinalIgnoreCase);
    }

    static async Task<bool> GitClone(string repoUrl, string? commit, string destDir)
    {
        try
        {
            // Shallow clone (depth 1) if we have a commit, otherwise shallow clone default branch
            var cloneArgs = commit is not null
                ? $"clone --depth 1 --no-tags \"{repoUrl}\" \"{destDir}\""
                : $"clone --depth 1 --no-tags \"{repoUrl}\" \"{destDir}\"";

            var result = await RunGit(cloneArgs, timeoutSeconds: 120);
            if (!result.Success) return false;

            // If we have a specific commit, fetch and checkout
            if (commit is not null)
            {
                // The shallow clone may not contain the commit.
                // Try checkout first, then fetch if needed.
                var checkout = await RunGit($"-C \"{destDir}\" checkout {commit}", timeoutSeconds: 30);
                if (!checkout.Success)
                {
                    // Fetch the specific commit (requires server support for fetch-by-sha)
                    var fetch = await RunGit(
                        $"-C \"{destDir}\" fetch --depth 1 origin {commit}", timeoutSeconds: 60);
                    if (fetch.Success)
                    {
                        await RunGit($"-C \"{destDir}\" checkout {commit}", timeoutSeconds: 30);
                    }
                    // If fetch-by-sha not supported, we still have HEAD which is likely close enough
                }
            }

            // Remove .git directory to save space and make it truly vendored
            var gitDir = Path.Combine(destDir, ".git");
            if (Directory.Exists(gitDir))
            {
                // Use rm -rf as .git contains read-only files
                await RunProcess("rm", $"-rf \"{gitDir}\"", timeoutSeconds: 30);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    static (string Owner, string Repo)? TryParseGitHubRepo(string url)
    {
        // Match https://github.com/owner/repo[.git] or git://github.com/owner/repo[.git]
        url = url.Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        // Try URI parsing first
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
                return (segments[0], segments[1]);
        }

        return null;
    }

    static async Task<bool> DownloadGitHubTarball(string owner, string repo, string? commit, string destDir)
    {
        try
        {
            var reference = commit ?? "HEAD";
            var tarballUrl = $"https://github.com/{owner}/{repo}/archive/{reference}.tar.gz";

            Directory.CreateDirectory(destDir);

            // curl -sL follows redirects; tar --strip-components=1 removes the top-level dir
            var result = await RunProcess("sh",
                $"-c 'curl -sfL \"{tarballUrl}\" | tar xz --strip-components=1 -C \"{destDir}\"'",
                timeoutSeconds: 120);

            if (result.Success) return true;

            // Clean up empty dir on failure
            if (Directory.Exists(destDir) && !Directory.EnumerateFileSystemEntries(destDir).Any())
                Directory.Delete(destDir);

            return false;
        }
        catch
        {
            return false;
        }
    }

    static async Task<ProcessResult> RunGit(string arguments, int timeoutSeconds)
    {
        return await RunProcess("git", arguments, timeoutSeconds);
    }

    static async Task<ProcessResult> RunProcess(string exe, string arguments, int timeoutSeconds)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                proc.Kill(entireProcessTree: true);
                return new ProcessResult(false, "", "timeout");
            }

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            return new ProcessResult(proc.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcessResult(false, "", ex.Message);
        }
    }

    static string NormalizeRepoUrl(string url)
    {
        // Normalize git URLs: remove .git suffix, trailing slashes, lowercase host
        url = url.Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];
        return url.ToLowerInvariant();
    }

    static string SanitizeDirName(string name)
    {
        return name.Replace('/', '_').Replace('\\', '_');
    }

    record ProcessResult(bool Success, string Stdout, string Stderr);
}
