using System;
using System.Runtime.CompilerServices;

namespace McpLense.UnitTests;

/// <summary>
/// Disables McpLense's profile auto-discovery during the unit test run so a developer's
/// user-side profile (e.g. <c>$XDG_CONFIG_HOME/McpLense/McpLense.Profiles.json</c>) can't bleed
/// into tests that exercise the auto-discovery path.
/// </summary>
internal static class TestModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        Environment.SetEnvironmentVariable("MCPLENSE_NO_PROFILE_AUTO_DISCOVERY", "1");
    }
}
