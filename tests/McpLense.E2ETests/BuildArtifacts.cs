using System;
using System.IO;
using System.Linq;

namespace McpLense.E2ETests;

internal static class BuildArtifacts
{
    private static readonly Lazy<string> _repoRoot = new(LocateRepoRoot);
    private static readonly Lazy<string> _mainAppDll = new(() => LocateAssembly("McpLense", Path.Combine("src", "McpLense", "bin")));
    private static readonly Lazy<string> _testServerDll = new(() => LocateAssembly("McpLense.TestServer", Path.Combine("tests", "McpLense.TestServer", "bin")));
    private static readonly Lazy<string> _testHttpServerDll = new(() => LocateAssembly("McpLense.TestHttpServer", Path.Combine("tests", "McpLense.TestHttpServer", "bin")));

    public static string RepoRoot => _repoRoot.Value;

    public static string MainAppDll => _mainAppDll.Value;

    public static string TestServerDll => _testServerDll.Value;

    public static string TestHttpServerDll => _testHttpServerDll.Value;

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "McpLense.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate repository root (containing src/McpLense.slnx) starting from '{AppContext.BaseDirectory}'.");
    }

    private static string LocateAssembly(string assemblyName, string binRelativePath)
    {
        var configuration = InferConfiguration();
        var targetFramework = InferTargetFramework();

        var preferredPath = Path.Combine(
            RepoRoot,
            binRelativePath,
            configuration,
            targetFramework,
            $"{assemblyName}.dll");

        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        var binRoot = Path.Combine(RepoRoot, binRelativePath);
        if (!Directory.Exists(binRoot))
        {
            throw new InvalidOperationException(
                $"Build output for '{assemblyName}' was not found. Expected '{preferredPath}' or any '{binRoot}\\**\\{assemblyName}.dll'.");
        }

        var fallback = Directory.EnumerateFiles(binRoot, $"{assemblyName}.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (fallback is null)
        {
            throw new InvalidOperationException(
                $"Build output for '{assemblyName}' was not found under '{binRoot}'.");
        }

        return fallback;
    }

    private static string InferConfiguration()
    {
        var baseDirectory = AppContext.BaseDirectory.Replace('\\', '/').TrimEnd('/');
        var segments = baseDirectory.Split('/');
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            if (string.Equals(segments[index], "bin", StringComparison.OrdinalIgnoreCase) && index + 1 < segments.Length)
            {
                return segments[index + 1];
            }
        }

        return "Debug";
    }

    private static string InferTargetFramework()
    {
        var baseDirectory = AppContext.BaseDirectory.Replace('\\', '/').TrimEnd('/');
        var segments = baseDirectory.Split('/');
        var last = segments.LastOrDefault();

        if (!string.IsNullOrWhiteSpace(last) && last.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return last;
        }

        return "net10.0";
    }
}
