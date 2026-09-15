namespace DotnetVendor;

using System.Text.Json;
using System.Text.Json.Serialization;

public static class ManifestWriter
{
    public static async Task<VendorManifest?> ReadAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
            return JsonSerializer.Deserialize<VendorManifest>(json, options);
        }
        catch
        {
            return null;
        }
    }

    public static async Task WriteAsync(
        string path,
        List<RepoInfo> infos,
        List<string> excludePrefixes,
        bool noDefaultExcludes)
    {
        var manifest = new VendorManifest
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            ToolVersion = "1.1.0",
            ExcludePrefixes = excludePrefixes,
            NoDefaultExcludes = noDefaultExcludes,
            Packages = infos.Select(i => new VendorManifestEntry
            {
                PackageId = i.PackageId,
                Version = i.Version,
                RepositoryUrl = i.RepoUrl,
                Commit = i.Commit,
                RepositoryType = i.RepoType,
                DiscoverySource = i.Source,
            }).ToList(),
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        var json = JsonSerializer.Serialize(manifest, options);
        await File.WriteAllTextAsync(path, json);
    }
}

public class VendorManifest
{
    public DateTimeOffset GeneratedAt { get; set; }
    public string ToolVersion { get; set; } = "";
    public List<string> ExcludePrefixes { get; set; } = [];
    public bool NoDefaultExcludes { get; set; }
    public List<VendorManifestEntry> Packages { get; set; } = [];
}

public class VendorManifestEntry
{
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string? RepositoryUrl { get; set; }
    public string? Commit { get; set; }
    public string? RepositoryType { get; set; }
    public string DiscoverySource { get; set; } = "";
}
