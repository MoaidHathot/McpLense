using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth;

/// <summary>
/// Locks in the public surface of <see cref="ProfileLoader"/> so library consumers can
/// reproduce exactly the same merged profile set the CLI loads, including environment
/// expansion via <see cref="EnvironmentExpander"/>.
/// </summary>
public class ProfileLoaderPublicApiTests
{
    [Fact]
    public async Task LoadFromFileAsync_ExpandsEnv_AndReturnsParsedProfiles()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, """
            {
              "authProfiles": [
                { "name": "demo", "auth": { "type": "bearer", "token": "${MY_TOKEN:-fallback}" } }
              ]
            }
            """);

            var expander = new EnvironmentExpander(_ => null); // MY_TOKEN unset -> fallback
            var profiles = await ProfileLoader.LoadFromFileAsync(tmp, expander, CancellationToken.None);

            profiles.Count.ShouldBe(1);
            profiles[0].Name.ShouldBe("demo");
            profiles[0].Auth.Kind.ShouldBe(AuthKind.Bearer);
            profiles[0].Auth.Token.ShouldBe("fallback");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task LoadFromFileAsync_MissingPath_ThrowsUserInput()
    {
        await Should.ThrowAsync<UserInputException>(() =>
            ProfileLoader.LoadFromFileAsync(@"P:\does-not-exist.json"));
    }

    [Fact]
    public async Task LoadFromXdgAsync_NoDiscovery_ReturnsEmpty()
    {
        // Auto-discovery kill-switch keeps the default-config path purely opt-in for tests.
        Environment.SetEnvironmentVariable(DefaultConfigPaths.DisableAutoDiscoveryEnvVar, "1");
        try
        {
            var profiles = await ProfileLoader.LoadFromXdgAsync(CancellationToken.None);
            profiles.ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DefaultConfigPaths.DisableAutoDiscoveryEnvVar, null);
        }
    }
}
