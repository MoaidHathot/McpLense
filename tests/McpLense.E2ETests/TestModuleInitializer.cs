using System;
using System.Runtime.CompilerServices;

namespace McpLense.E2ETests;

/// <summary>
/// Disables McpLense's profile auto-discovery for E2E test subprocesses. The env var is
/// inherited by every <c>mcplense</c> child process spawned via <c>CliRunner</c> /
/// <c>dotnet exec</c>, so the user's XDG profile never bleeds into the test run.
/// </summary>
internal static class TestModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        Environment.SetEnvironmentVariable("MCPLENSE_NO_PROFILE_AUTO_DISCOVERY", "1");
    }
}
