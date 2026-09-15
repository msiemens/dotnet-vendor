namespace DotnetVendor;

using System.Xml.Linq;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

public record RepoInfo(
    string PackageId,
    string Version,
    string? RepoUrl,
    string? Commit,
    string? RepoType,
    string Source  // "nuspec-local", "nuget-api", "none"
);

public static class RepositoryDiscoverer
{
    public static async Task<List<RepoInfo>> DiscoverAllAsync(
        List<ResolvedPackage> packages, int parallelism)
    {
        var semaphore = new SemaphoreSlim(parallelism);
        var tasks = packages.Select(async pkg =>
        {
            await semaphore.WaitAsync();
            try
            {
                return await DiscoverOne(pkg);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    static async Task<RepoInfo> DiscoverOne(ResolvedPackage pkg)
    {
        // Strategy 1: Read from local cached nuspec
        var local = TryReadLocalNuspec(pkg);
        if (local is not null && local.RepoUrl is not null)
            return local;

        // Strategy 2: Query NuGet.org API for repository metadata
        var remote = await TryQueryNuGetApi(pkg);
        if (remote is not null && remote.RepoUrl is not null)
            return remote;

        // Strategy 3: Try to infer from projectUrl
        var inferred = await TryInferFromProjectUrl(pkg);
        if (inferred is not null)
            return inferred;

        return new RepoInfo(pkg.Id, pkg.Version, null, null, null, "none");
    }

    static RepoInfo? TryReadLocalNuspec(ResolvedPackage pkg)
    {
        if (pkg.NupkgPath is null) return null;

        // Look for .nuspec file in the cached package directory
        var nuspecPath = Path.Combine(pkg.NupkgPath, $"{pkg.Id.ToLowerInvariant()}.nuspec");
        if (!File.Exists(nuspecPath))
        {
            // Try case-insensitive search
            var candidates = Directory.EnumerateFiles(pkg.NupkgPath, "*.nuspec").ToList();
            nuspecPath = candidates.FirstOrDefault();
        }

        if (nuspecPath is null || !File.Exists(nuspecPath)) return null;

        try
        {
            var xml = XDocument.Load(nuspecPath);
            var ns = xml.Root?.Name.Namespace ?? XNamespace.None;
            var metadata = xml.Root?.Element(ns + "metadata");
            if (metadata is null) return null;

            var repoEl = metadata.Element(ns + "repository");
            if (repoEl is not null)
            {
                var url = repoEl.Attribute("url")?.Value;
                var commit = repoEl.Attribute("commit")?.Value;
                var type = repoEl.Attribute("type")?.Value;

                if (!string.IsNullOrWhiteSpace(url))
                {
                    return new RepoInfo(pkg.Id, pkg.Version, url, commit, type, "nuspec-local");
                }
            }

            // Fallback: check projectUrl for github/gitlab patterns
            var projectUrl = metadata.Element(ns + "projectUrl")?.Value;
            if (IsGitRepoUrl(projectUrl))
            {
                return new RepoInfo(pkg.Id, pkg.Version, projectUrl, null, "git", "nuspec-local-projecturl");
            }
        }
        catch
        {
            // Malformed nuspec, skip
        }

        return null;
    }

    static async Task<RepoInfo?> TryQueryNuGetApi(ResolvedPackage pkg)
    {
        try
        {
            var cache = new SourceCacheContext();
            var repository = NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<PackageMetadataResource>();

            var metadata = await resource.GetMetadataAsync(
                pkg.Id,
                includePrerelease: true,
                includeUnlisted: true,
                cache,
                NullLogger.Instance,
                CancellationToken.None);

            var match = metadata
                .FirstOrDefault(m => m.Identity.Version.ToString() == pkg.Version)
                ?? metadata.LastOrDefault();

            if (match is null) return null;

            // The PackageMetadataResource doesn't directly expose repository info in all cases.
            // Try the registration endpoint for richer metadata.
            return await TryRegistrationEndpoint(pkg);
        }
        catch
        {
            return null;
        }
    }

    static async Task<RepoInfo?> TryRegistrationEndpoint(ResolvedPackage pkg)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "dotnet-vendor/1.0");

            // NuGet v3 registration endpoint
            var regUrl = $"https://api.nuget.org/v3/registration5-gz-semver2/" +
                         $"{pkg.Id.ToLowerInvariant()}/{pkg.Version.ToLowerInvariant()}.json";

            var response = await http.GetAsync(regUrl);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            // Try to get catalogEntry -> repository or projectUrl
            if (doc.RootElement.TryGetProperty("catalogEntry", out var catalogEntry))
            {
                return TryParseCatalogEntry(pkg, catalogEntry);
            }

            // Sometimes the data is directly in the root
            return TryParseCatalogEntry(pkg, doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    static RepoInfo? TryParseCatalogEntry(ResolvedPackage pkg, System.Text.Json.JsonElement entry)
    {
        // Try repository URL from catalog
        if (entry.TryGetProperty("repository", out var repo) &&
            repo.TryGetProperty("url", out var repoUrl))
        {
            var url = repoUrl.GetString();
            string? commit = null;
            if (repo.TryGetProperty("commit", out var commitEl))
                commit = commitEl.GetString();
            string? type = null;
            if (repo.TryGetProperty("type", out var typeEl))
                type = typeEl.GetString();

            if (!string.IsNullOrWhiteSpace(url))
                return new RepoInfo(pkg.Id, pkg.Version, url, commit, type, "nuget-api");
        }

        // Fallback to projectUrl
        if (entry.TryGetProperty("projectUrl", out var projUrl))
        {
            var url = projUrl.GetString();
            if (IsGitRepoUrl(url))
                return new RepoInfo(pkg.Id, pkg.Version, url, null, "git", "nuget-api-projecturl");
        }

        return null;
    }

    static async Task<RepoInfo?> TryInferFromProjectUrl(ResolvedPackage pkg)
    {
        // As a last resort, try the NuGet catalog for projectUrl pointing to GitHub/GitLab
        // This is already attempted above; this method is a placeholder for future strategies
        // like searching GitHub for the package name.
        await Task.CompletedTask;
        return null;
    }

    static bool IsGitRepoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("github.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("gitlab.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("bitbucket.org", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("codeberg.org", StringComparison.OrdinalIgnoreCase) ||
               url.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
    }
}
