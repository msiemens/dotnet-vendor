namespace DotnetVendor;

using System.Diagnostics;

public static class Decompiler
{
    static bool? _ilspyAvailable;

    public static async Task<bool> TryDecompile(string packageId, string version, string outputDir)
    {
        // Check if ilspycmd is available (once)
        _ilspyAvailable ??= await CheckIlspyAvailable();
        if (!_ilspyAvailable.Value)
        {
            Console.Error.WriteLine(
                "    ilspycmd not found. Install with: dotnet tool install -g ilspycmd");
            return false;
        }

        // Find the DLL(s) in the NuGet cache
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var packageDir = Path.Combine(home, ".nuget", "packages",
            packageId.ToLowerInvariant(), version.ToLowerInvariant());

        if (!Directory.Exists(packageDir))
        {
            Console.Error.WriteLine($"\n    package cache not found: {packageDir}");
            return false;
        }

        // Find the best TFM directory (prefer net8.0 > net6.0 > netstandard2.1 > netstandard2.0 > any)
        var libDir = FindBestTfmDir(packageDir);
        if (libDir is null)
        {
            Console.Error.WriteLine($"\n    no TFM directory with DLLs found in {packageDir}");
            return false;
        }

        var dlls = Directory.GetFiles(libDir, "*.dll");
        if (dlls.Length == 0)
        {
            Console.Error.WriteLine($"\n    no DLLs found in {libDir}");
            return false;
        }

        Directory.CreateDirectory(outputDir);

        bool anySuccess = false;
        foreach (var dll in dlls)
        {
            var dllName = Path.GetFileNameWithoutExtension(dll);
            var dllOutputDir = dlls.Length == 1
                ? outputDir
                : Path.Combine(outputDir, dllName);

            try
            {
                using var proc = new Process();
                proc.StartInfo = new ProcessStartInfo
                {
                    FileName = "ilspycmd",
                    Arguments = $"-p --nested-directories --no-dead-code -o \"{dllOutputDir}\" \"{dll}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                proc.Start();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    proc.Kill(true);
                    Console.Error.WriteLine($"\n    ilspycmd timed out for {dll}");
                    continue;
                }

                if (proc.ExitCode == 0)
                    anySuccess = true;
                else
                {
                    var stderr = await proc.StandardError.ReadToEndAsync();
                    Console.Error.WriteLine($"\n    ilspycmd failed (exit {proc.ExitCode}) for {dll}");
                    if (!string.IsNullOrWhiteSpace(stderr))
                        Console.Error.WriteLine($"    {stderr.Trim()}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\n    ilspycmd error for {dll}: {ex.Message}");
            }
        }

        // Write a marker file indicating this was decompiled
        if (anySuccess)
        {
            await File.WriteAllTextAsync(
                Path.Combine(outputDir, ".decompiled"),
                $"# This source was decompiled from {packageId} v{version} using ILSpy.\n" +
                $"# It may differ from the original source code.\n" +
                $"# Comments, original formatting, and some variable names are not preserved.\n");
        }

        return anySuccess;
    }

    static string? FindBestTfmDir(string packageDir)
    {
        var libDir = Path.Combine(packageDir, "lib");
        if (!Directory.Exists(libDir))
        {
            // Maybe the DLLs are directly in the package dir
            if (Directory.GetFiles(packageDir, "*.dll").Length > 0)
                return packageDir;
            return null;
        }

        var tfmPreference = new[]
        {
            "net9.0", "net8.0", "net7.0", "net6.0",
            "netstandard2.1", "netstandard2.0", "netstandard1.6",
            "net48", "net472", "net462", "net461",
            "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1",
        };

        foreach (var tfm in tfmPreference)
        {
            var dir = Path.Combine(libDir, tfm);
            if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.dll").Length > 0)
                return dir;
        }

        // Just pick the first directory that has DLLs
        foreach (var dir in Directory.EnumerateDirectories(libDir))
        {
            if (Directory.GetFiles(dir, "*.dll").Length > 0)
                return dir;
        }

        return null;
    }

    static async Task<bool> CheckIlspyAvailable()
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = "ilspycmd",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc.Start();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
