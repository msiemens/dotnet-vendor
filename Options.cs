namespace DotnetVendor;

public class Options
{
    public string ProjectDir { get; init; } = ".";
    public string OutputDir { get; init; } = "vendor";
    public bool FallbackDecompile { get; init; } = true;
    public int Parallelism { get; init; } = 4;
    public List<string> ExcludePrefixes { get; init; } = [];
    public bool ExcludePrefixesExplicit { get; init; }
    public bool NoDefaultExcludes { get; init; }
    public bool NoDefaultExcludesExplicit { get; init; }
    public bool DryRun { get; init; }
    public bool Update { get; init; }

    public static Options? Parse(string[] args)
    {
        var opts = new Options();
        string? projectDir = null;
        string? outputDir = null;
        bool? fallbackDecompile = null;
        int? parallelism = null;
        var excludePrefixes = new List<string>();
        bool noDefaultExcludes = false;
        bool dryRun = false;
        bool update = false;

        // Accept `update` as a subcommand equivalent to `--update`.
        if (args.Length > 0 && args[0] == "update")
        {
            update = true;
            args = args[1..];
        }

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    PrintHelp();
                    return null;

                case "-o" or "--output":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("Missing value for --output"); return null; }
                    outputDir = args[++i];
                    break;

                case "--no-decompile":
                    fallbackDecompile = false;
                    break;

                case "--decompile":
                    fallbackDecompile = true;
                    break;

                case "-j" or "--parallelism":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("Missing value for --parallelism"); return null; }
                    if (!int.TryParse(args[++i], out var p) || p < 1) { Console.Error.WriteLine("Invalid parallelism value"); return null; }
                    parallelism = p;
                    break;

                case "-e" or "--exclude":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("Missing value for --exclude"); return null; }
                    excludePrefixes.Add(args[++i]);
                    break;

                case "--no-default-excludes":
                    noDefaultExcludes = true;
                    break;

                case "--dry-run" or "-n":
                    dryRun = true;
                    break;

                case "--update" or "-u":
                    update = true;
                    break;

                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option: {args[i]}");
                        PrintHelp();
                        return null;
                    }
                    projectDir = args[i];
                    break;
            }
        }

        return new Options
        {
            ProjectDir = projectDir ?? opts.ProjectDir,
            OutputDir = outputDir ?? opts.OutputDir,
            FallbackDecompile = fallbackDecompile ?? opts.FallbackDecompile,
            Parallelism = parallelism ?? opts.Parallelism,
            ExcludePrefixes = excludePrefixes,
            ExcludePrefixesExplicit = excludePrefixes.Count > 0,
            NoDefaultExcludes = noDefaultExcludes,
            NoDefaultExcludesExplicit = noDefaultExcludes,
            DryRun = dryRun,
            Update = update,
        };
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
            dotnet-vendor — vendor NuGet dependency source code into your repo

            Like `cargo vendor` or `go mod vendor`, but for .NET.
            Downloads source from git repos (via nuspec repository metadata / Source Link),
            with optional ILSpy decompilation fallback for packages without source.

            USAGE:
                dotnet-vendor [OPTIONS] [PROJECT_DIR]
                dotnet-vendor update [OPTIONS] [PROJECT_DIR]

            ARGUMENTS:
                PROJECT_DIR    Path to the project/solution directory (default: .)

            OPTIONS:
                -o, --output <DIR>      Output directory (default: vendor)
                --decompile             Enable ILSpy decompile fallback (default)
                --no-decompile          Disable decompile fallback; skip packages without repo URL
                -e, --exclude <PREFIX>  Exclude packages whose ID starts with PREFIX (repeatable,
                                        e.g. -e Azure excludes Azure.Storage.Blobs, Azure.Identity, …)
                --no-default-excludes   Disable the built-in exclusion list (runtime, SDK, framework)
                -n, --dry-run           Show what would be done without cloning or decompiling
                -u, --update            Refresh packages whose commit/version differs from the
                                        existing vendor-manifest.json (equivalent to the `update`
                                        subcommand). Without this flag, directories that already
                                        exist are skipped unconditionally. Also re-applies
                                        --exclude / --no-default-excludes from the previous
                                        manifest unless explicitly overridden on the CLI.
                -j, --parallelism <N>   Max concurrent metadata fetches (default: 4)
                -h, --help              Show this help

            WORKFLOW:
                1. Run `dotnet restore` in your project first
                2. Run `dotnet-vendor` to download source into ./vendor/
                3. Point your AI agent at the ./vendor/ directory

            PREREQUISITES:
                - git (on PATH)
                - dotnet restore (run before this tool)
                - ilspycmd (optional, for decompile fallback):
                  dotnet tool install -g ilspycmd

            OUTPUT PREFIXES:
                [tar]   Downloaded as GitHub tarball (fastest)
                [git]   Cloned via git (non-GitHub repos, or tarball fallback)
                [dec]   Decompiled with ILSpy (no source repo found)
                [ref]   Shares a repository with another package (see .repo-ref file)
                [skip]  Already vendored or no repository URL

            WHAT IT DOES:
                1. Reads project.assets.json to find all NuGet dependencies
                2. Inspects each package's .nuspec for repository URL + commit
                3. Falls back to NuGet.org API metadata if nuspec lacks repo info
                4. Git-clones each unique repository at the right commit
                5. For packages without a repo URL, decompiles with ILSpy (if enabled)
                6. Writes vendor-manifest.json with provenance info
            """);
    }
}
