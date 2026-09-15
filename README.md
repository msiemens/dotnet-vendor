# dotnet-vendor

**`cargo vendor` / `go mod vendor` for .NET** — download the source code of all your NuGet dependencies into a local `vendor/` directory.

Designed for AI coding agents that need to read dependency source code, but also useful for auditing and code review.

## How it works

1. Read the resolved package list from `obj/project.assets.json` and drop [excluded packages](#excluded-packages).
2. For each package, find its source repository (see [discovery](#repository-discovery)).
3. Download each repository once: GitHub repos as a tarball, other hosts via shallow `git clone`.
4. Decompile packages without a repository (or whose download failed) with `ilspycmd`.
5. Write `vendor-manifest.json`.

## Installation

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a Unix-like environment (Linux, macOS, WSL) with `git`, `curl` and `tar`.

```bash
git clone https://github.com/yourname/dotnet-vendor
cd dotnet-vendor
dotnet pack -c Release
dotnet tool install --global --add-source ./bin/Release dotnet-vendor

# Optional, for the decompile fallback
dotnet tool install --global ilspycmd
```

On Linux, add `~/.dotnet/tools` to your `PATH`. If the SDK isn't installed at `/usr/share/dotnet` (NixOS, Homebrew, custom `dotnet-install.sh` prefixes) and the tool fails with *"You must install .NET to run this application"*, also set `DOTNET_ROOT`:

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
export DOTNET_ROOT="$(dirname "$(realpath "$(which dotnet)")")"
```

## Usage

```bash
cd /path/to/your/project
dotnet restore
dotnet-vendor
```

```
dotnet-vendor [OPTIONS] [PROJECT_DIR]
dotnet-vendor update [OPTIONS] [PROJECT_DIR]

ARGUMENTS:
    PROJECT_DIR    Project directory (default: .)

OPTIONS:
    -o, --output <DIR>      Output directory (default: vendor)
    --decompile             Enable ILSpy decompile fallback (default)
    --no-decompile          Disable decompile fallback
    -e, --exclude <PREFIX>  Exclude packages whose ID starts with PREFIX (repeatable)
    --no-default-excludes   Disable the built-in exclusion list
    -n, --dry-run           Show what would be done without writing anything
    -u, --update            Refresh packages whose commit or version changed
                            (same as the `update` subcommand)
    -j, --parallelism <N>   Max concurrent metadata fetches (default: 4)
    -h, --help              Show help
```

`PROJECT_DIR` is the directory containing a project's `obj/project.assets.json` (or its `.csproj`/`.fsproj`). Each run vendors one project.

Packages that are already in the output directory are kept as is. `update` compares them against the previous manifest (by commit, or by version if there is no commit) and re-downloads the ones that changed. It reuses the previous run's exclusion settings unless you pass new ones.

### Excluded packages

Runtime, SDK and framework packages are excluded by default. Built-in prefixes:

`Microsoft.NETCore.App`, `Microsoft.AspNetCore.App`, `Microsoft.WindowsDesktop.App`, `NETStandard.Library`, `runtime.`, `Microsoft.AspNetCore.`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.`, `Microsoft.Bcl.`, `System.`

## Output

```
vendor/
├── Newtonsoft.Json/          # upstream source
├── SomeClosedSourceLib/      # decompiled
│   ├── .decompiled           # marker
│   └── SomeClosedSourceLib.csproj
├── Polly/                    # repository shared by Polly and Polly.Core
├── Polly.Core.repo-ref       # points to Polly/
└── vendor-manifest.json
```

Packages from the same repository are downloaded once, under the first package name (preferring one with a commit), and the others get a `.repo-ref` file. Decompiled packages with several assemblies get one subdirectory per assembly.

`vendor-manifest.json` records the discovered repository for every package:

```json
{
  "generatedAt": "2026-04-13T12:00:00+00:00",
  "toolVersion": "1.1.0",
  "excludePrefixes": [],
  "noDefaultExcludes": false,
  "packages": [
    {
      "packageId": "Newtonsoft.Json",
      "version": "13.0.3",
      "repositoryUrl": "https://github.com/JamesNK/Newtonsoft.Json.git",
      "commit": "0a2e291c0d9c0c7675d445703e51750363a549ef",
      "repositoryType": "git",
      "discoverySource": "nuspec-local"
    },
    {
      "packageId": "SomeClosedSourceLib",
      "version": "2.1.0",
      "discoverySource": "none"
    }
  ]
}
```

## Repository discovery

The first strategy that yields a URL wins:

| `discoverySource` | Source | Commit |
|-------------------|--------|--------|
| `nuspec-local` | `<repository>` in the cached `.nuspec` (`~/.nuget/packages`) | ✅ if published |
| `nuspec-local-projecturl` | `<projectUrl>` in the cached `.nuspec` | ❌ |
| `nuget-api` | `repository` from the NuGet.org registration API | ✅ if published |
| `nuget-api-projecturl` | `projectUrl` from the NuGet.org registration API | ❌ |
| `none` | — | — |

`projectUrl` is only used if it points to GitHub, GitLab, Azure DevOps, Bitbucket, Codeberg or ends in `.git`. With a commit, that exact commit is downloaded; without one, the default branch. Packages built with [Source Link](https://github.com/dotnet/sourcelink) publish both URL and commit.

## For AI agents

```
The source code of all NuGet dependencies is in ./vendor/.
Directories containing a .decompiled file are decompiled, not original source.
<PackageId>.repo-ref files point to the directory containing that package.
```

## License

MIT
