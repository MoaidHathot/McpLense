using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using McpLense;
using McpLense.UnitTests.Helpers;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Configuration;

public class DefaultConfigPathsTests
{
    private static System.Func<string, string?> Env(IDictionary<string, string?> values)
        => name => values.TryGetValue(name, out var value) ? value : null;

    private static System.Func<OSPlatform, bool> AsOs(OSPlatform actual)
        => platform => platform == actual;

    [Fact]
    public void ResolveRoot_XdgConfigHomeSet_OnAnyPlatform_PrefersXdg()
    {
        var env = Env(new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = "/custom/xdg",
            ["APPDATA"] = "C:\\AppData",
            ["HOME"] = "/home/me"
        });

        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.Windows))
            .ShouldBe(Path.Combine("/custom/xdg", "McpLense"));
        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.Linux))
            .ShouldBe(Path.Combine("/custom/xdg", "McpLense"));
        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.OSX))
            .ShouldBe(Path.Combine("/custom/xdg", "McpLense"));
    }

    [Fact]
    public void ResolveRoot_XdgUnset_Windows_FallsBackToAppData()
    {
        var env = Env(new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = null,
            ["APPDATA"] = "C:\\Users\\Test\\AppData\\Roaming",
            ["HOME"] = "/home/me"
        });

        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.Windows))
            .ShouldBe(Path.Combine("C:\\Users\\Test\\AppData\\Roaming", "McpLense"));
    }

    [Fact]
    public void ResolveRoot_XdgUnset_Unix_FallsBackToHomeConfig()
    {
        var env = Env(new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = null,
            ["APPDATA"] = null,
            ["HOME"] = "/home/me"
        });

        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.Linux))
            .ShouldBe(Path.Combine("/home/me", ".config", "McpLense"));
    }

    [Fact]
    public void ResolveRoot_XdgEmpty_TreatedAsUnset()
    {
        var env = Env(new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = "",
            ["APPDATA"] = "C:\\AppData"
        });

        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.Windows))
            .ShouldBe(Path.Combine("C:\\AppData", "McpLense"));
    }

    [Fact]
    public void ResolveRoot_XdgWhitespace_TreatedAsUnset()
    {
        var env = Env(new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = "   ",
            ["HOME"] = "/home/me"
        });

        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.Linux))
            .ShouldBe(Path.Combine("/home/me", ".config", "McpLense"));
    }

    [Fact]
    public void ResolveRoot_NoXdgNoAppData_OnWindows_ReturnsNull()
    {
        var env = Env(new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = null,
            ["APPDATA"] = null
        });

        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.Windows)).ShouldBeNull();
    }

    [Fact]
    public void ResolveRoot_NoXdgNoHome_OnUnix_ReturnsNull()
    {
        var env = Env(new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = null,
            ["HOME"] = null
        });

        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.Linux)).ShouldBeNull();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("yes")]
    [InlineData("on")]
    public void ResolveRoot_DisableAutoDiscoveryEnvSet_ReturnsNull(string value)
    {
        var env = Env(new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = "/custom/xdg",
            ["APPDATA"] = "C:\\AppData",
            ["HOME"] = "/home/me",
            [DefaultConfigPaths.DisableAutoDiscoveryEnvVar] = value
        });

        // Even when XDG/APPDATA/HOME are set, the kill-switch wins.
        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.Windows)).ShouldBeNull();
        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.Linux)).ShouldBeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRoot_DisableAutoDiscoveryEnvFalsy_DoesNotDisable(string value)
    {
        var env = Env(new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = "/custom/xdg",
            [DefaultConfigPaths.DisableAutoDiscoveryEnvVar] = value
        });

        DefaultConfigPaths.ResolveRoot(env, AsOs(OSPlatform.Linux))
            .ShouldBe(Path.Combine("/custom/xdg", "McpLense"));
    }

    [Fact]
    public void EnumerateProfileFiles_NullRoot_ReturnsEmpty()
    {
        DefaultConfigPaths.EnumerateProfileFiles(null).Count.ShouldBe(0);
        DefaultConfigPaths.EnumerateProfileFiles("").Count.ShouldBe(0);
    }

    [Fact]
    public void EnumerateProfileFiles_NonexistentRoot_ReturnsEmpty()
    {
        var bogus = Path.Combine(Path.GetTempPath(), $"mcplense-bogus-{System.Guid.NewGuid():N}");

        DefaultConfigPaths.EnumerateProfileFiles(bogus).Count.ShouldBe(0);
    }

    [Fact]
    public void EnumerateProfileFiles_RootFileOnly_ReturnsIt()
    {
        using var dir = new TempDirectory();
        var rootFile = Path.Combine(dir.Path, DefaultConfigPaths.ProfilesFileName);
        File.WriteAllText(rootFile, "{}");

        var result = DefaultConfigPaths.EnumerateProfileFiles(dir.Path);

        result.Count.ShouldBe(1);
        result[0].ShouldBe(rootFile);
    }

    [Fact]
    public void EnumerateProfileFiles_SubdirOnly_ReturnsAlphabetised()
    {
        using var dir = new TempDirectory();
        var subDir = Path.Combine(dir.Path, DefaultConfigPaths.ProfilesSubdirectoryName);
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "github.json"), "{}");
        File.WriteAllText(Path.Combine(subDir, "agent365.json"), "{}");
        // Non-json file should be ignored.
        File.WriteAllText(Path.Combine(subDir, "README.md"), "ignored");

        var result = DefaultConfigPaths.EnumerateProfileFiles(dir.Path);

        result.Count.ShouldBe(2);
        result[0].ShouldEndWith("agent365.json");
        result[1].ShouldEndWith("github.json");
    }

    [Fact]
    public void EnumerateProfileFiles_RootFilePlusSubdir_ReturnsRootFirstThenSubdirSorted()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, DefaultConfigPaths.ProfilesFileName), "{}");

        var subDir = Path.Combine(dir.Path, DefaultConfigPaths.ProfilesSubdirectoryName);
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "z.json"), "{}");
        File.WriteAllText(Path.Combine(subDir, "a.json"), "{}");

        var result = DefaultConfigPaths.EnumerateProfileFiles(dir.Path);

        result.Count.ShouldBe(3);
        result[0].ShouldEndWith(DefaultConfigPaths.ProfilesFileName);
        result[1].ShouldEndWith("a.json");
        result[2].ShouldEndWith("z.json");
    }
}
