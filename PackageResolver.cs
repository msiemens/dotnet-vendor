namespace DotnetVendor;

using System.Text.Json;

public record ResolvedPackage(string Id, string Version, string? NupkgPath);

public static class PackageResolver
{
    public static async Task<List<ResolvedPackage>> ResolveAsync(
        string projectDir, IReadOnlyList<string>? excludePrefixes = null, bool noDefaultExcludes = false)
    {
        // Find project.assets.json (produced by dotnet restore)
        var assetsPath = FindAssetsFile(projectDir);
        if (assetsPath is null)
        {
            Console.Error.WriteLine(
                "Could not find project.assets.json. " +
                "Make sure you run 'dotnet restore' first, or point to a directory containing a .csproj/.sln.");
            return [];
        }

        Console.WriteLine($"Reading {assetsPath}");

        var json = await File.ReadAllTextAsync(assetsPath);
        using var doc = JsonDocument.Parse(json);

        var packages = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);

        // Parse the "libraries" section which has all resolved packages
        if (doc.RootElement.TryGetProperty("libraries", out var libraries))
        {
            foreach (var lib in libraries.EnumerateObject())
            {
                // Key is "PackageId/Version"
                var parts = lib.Name.Split('/', 2);
                if (parts.Length != 2) continue;

                var id = parts[0];
                var version = parts[1];

                // Only include "package" type (not "project")
                if (lib.Value.TryGetProperty("type", out var typeEl) &&
                    typeEl.GetString()?.Equals("package", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (!packages.ContainsKey(id))
                    {
                        var nupkgPath = FindCachedNupkg(id, version);
                        packages[id] = new ResolvedPackage(id, version, nupkgPath);
                    }
                }
            }
        }

        var allPrefixes = new List<string>();

        if (!noDefaultExcludes)
        {
            // Well-known runtime/SDK/framework packages that are not useful to vendor
            allPrefixes.AddRange([
                // Runtime and SDK ref packs
                "Microsoft.NETCore.App",
                "Microsoft.AspNetCore.App",
                "Microsoft.WindowsDesktop.App",
                "NETStandard.Library",
                "runtime.",
                // ASP.NET Core framework (lives in dotnet/dotnet monorepo)
                "Microsoft.AspNetCore.",
                // Entity Framework Core (lives in dotnet/dotnet monorepo)
                "Microsoft.EntityFrameworkCore",
                // Extensions (DI, logging, config, etc. — part of the framework)
                "Microsoft.Extensions.",
                // BCL polyfills
                "Microsoft.Bcl.",
                // System.* base class library packages (dotnet/runtime monorepo)
                "System.",
            ]);
        }

        if (excludePrefixes is { Count: > 0 })
            allPrefixes.AddRange(excludePrefixes);

        return packages.Values
            .Where(p => !allPrefixes.Any(prefix =>
                p.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string? FindAssetsFile(string dir)
    {
        dir = Path.GetFullPath(dir);

        // Direct path to obj/project.assets.json
        var objAssets = Path.Combine(dir, "obj", "project.assets.json");
        if (File.Exists(objAssets)) return objAssets;

        // Maybe dir itself is the obj folder
        var direct = Path.Combine(dir, "project.assets.json");
        if (File.Exists(direct)) return direct;

        // Search for .csproj/.fsproj and look in their obj dirs
        foreach (var proj in Directory.EnumerateFiles(dir, "*.csproj")
            .Concat(Directory.EnumerateFiles(dir, "*.fsproj")))
        {
            var projDir = Path.GetDirectoryName(proj)!;
            var assets = Path.Combine(projDir, "obj", "project.assets.json");
            if (File.Exists(assets)) return assets;
        }

        // Search for .sln and find project dirs
        foreach (var sln in Directory.EnumerateFiles(dir, "*.sln"))
        {
            // Simple: just recurse one level looking for obj/project.assets.json
            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                var assets = Path.Combine(subDir, "obj", "project.assets.json");
                if (File.Exists(assets)) return assets;
            }
        }

        return null;
    }

    static string? FindCachedNupkg(string packageId, string version)
    {
        // Standard NuGet global packages folder
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var packageDir = Path.Combine(home, ".nuget", "packages",
            packageId.ToLowerInvariant(), version.ToLowerInvariant());

        if (Directory.Exists(packageDir))
            return packageDir;

        return null;
    }
}
