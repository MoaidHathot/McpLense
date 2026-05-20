using McpLense.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Scanning;

/// <summary>
/// Tests for <see cref="ScanPluginLoader"/>: load isolation, IScanCheck discovery, error
/// handling. Uses the McpLense.TestPlugin assembly built alongside the tests as the
/// fixture - that project compiles against the same McpLense.dll the host loads, so the
/// AssemblyLoadContext can correctly share the IScanCheck identity.
/// </summary>
public class ScanPluginLoaderTests
{
    private static string PluginPath
    {
        get
        {
            // The test plugin builds into ../McpLense.TestPlugin/bin/<config>/<tfm>/McpLense.TestPlugin.dll.
            // We locate it relative to the executing test assembly's base directory so the
            // path is correct under both `dotnet test` and IDE-driven runs.
            var baseDir = AppContext.BaseDirectory;
            // baseDir = tests/McpLense.UnitTests/bin/<config>/<tfm>/
            // navigate up to bin/<config>/ peer, then across to TestPlugin's identical layout.
            var unitTestsDir = new DirectoryInfo(baseDir);
            var tfm = unitTestsDir.Name;             // e.g. net10.0
            var config = unitTestsDir.Parent!.Name;  // e.g. Release
            var testsRoot = unitTestsDir.Parent!.Parent!.Parent!.Parent!.FullName; // tests/
            return Path.Combine(testsRoot, "McpLense.TestPlugin", "bin", config, tfm, "McpLense.TestPlugin.dll");
        }
    }

    [Fact]
    public void LoadFromAssemblyPath_LoadsHelloCheck()
    {
        File.Exists(PluginPath).ShouldBeTrue($"expected plugin DLL at {PluginPath}; ensure the test plugin project built");

        var checks = ScanPluginLoader.LoadFromAssemblyPath(PluginPath);

        // HelloPluginCheck has a parameterless constructor -> picked up.
        // NeedsArgsCheck does NOT -> silently skipped.
        var ids = checks.Select(c => c.Id).ToArray();
        ids.ShouldContain("plugin.hello");
        ids.ShouldNotContain(id => id.StartsWith("needs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadedCheck_RunsInPipeline_ProducingItsData()
    {
        var checks = ScanPluginLoader.LoadFromAssemblyPath(PluginPath);
        var hello = checks.Single(c => c.Id == "plugin.hello");

        // HelloPluginCheck ignores the context entirely; minimal stub is fine.
        var ctx = ScanContext.ForTesting(
            server: new ResolvedServer(
                Name: "fixture",
                Kind: ConnectionKind.Http,
                Target: "https://example.invalid/mcp",
                Source: null,
                Command: null,
                CommandArguments: Array.Empty<string>(),
                WorkingDirectory: null,
                Environment: new Dictionary<string, string>(),
                Url: new Uri("https://example.invalid/mcp"),
                Transport: TransportPreference.Auto,
                Headers: new Dictionary<string, string>()),
            config: new ScanConfig(),
            services: new ServiceCollection().BuildServiceProvider());

        var outcome = await hello.RunAsync(ctx, CancellationToken.None);

        outcome.Ran.ShouldBeTrue();
        outcome.Data.ShouldNotBeNull();
        outcome.Data!.AsObject()["greeting"]!.ToString().ShouldBe("hello from a plugin");
    }

    [Fact]
    public void LoadFromAssemblyPath_MissingFile_Throws()
    {
        Should.Throw<ScanPluginException>(() => ScanPluginLoader.LoadFromAssemblyPath(Path.Combine(Path.GetTempPath(), "does-not-exist.dll")));
    }

    [Fact]
    public void LoadFromAssemblyPath_NullOrEmpty_Throws()
    {
        Should.Throw<ArgumentException>(() => ScanPluginLoader.LoadFromAssemblyPath(""));
    }

    [Fact]
    public void LoadFromDirectory_NonExistent_Throws()
    {
        Should.Throw<ScanPluginException>(() => ScanPluginLoader.LoadFromDirectory(Path.Combine(Path.GetTempPath(), $"mcplense-missing-{Guid.NewGuid():N}")));
    }
}
