using System;
using System.Runtime.CompilerServices;

namespace McpLense.IntegrationTests;

/// <summary>
/// Test-assembly bootstrap that disables McpLense's XDG/APPDATA profile auto-discovery for
/// the duration of the integration test run. Without this, a developer who happens to have
/// a profile in <c>$XDG_CONFIG_HOME/McpLense/McpLense.Profiles.json</c> (e.g. set up for daily
/// use) would see integration tests try to attach that profile to the local test HTTP server,
/// which in turn triggers an interactive MSAL browser flow during the test run. That is
/// unacceptable both for the developer (surprise browser pops) and for CI (no browser, no user).
/// </summary>
internal static class TestModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        Environment.SetEnvironmentVariable("MCPLENSE_NO_PROFILE_AUTO_DISCOVERY", "1");
    }
}
