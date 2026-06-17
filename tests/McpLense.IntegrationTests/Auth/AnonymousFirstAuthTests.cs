using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.IntegrationTests.Auth;

/// <summary>
/// Covers the "anonymous first" auth policy: an auto-picked profile is only a fallback. We connect
/// unauthenticated first and present credentials solely when the server refuses that attempt.
/// </summary>
public static class AnonymousFirstAuthTests
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static string WriteProfile(string name, string token)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcplense-profile-{Guid.NewGuid():N}.json");
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["authProfiles"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = name,
                    ["auth"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "bearer",
                        ["token"] = token
                    }
                }
            }
        }.ToJsonString();
        File.WriteAllText(path, json);
        return path;
    }

    private static ParsedCommand InspectWithProfile(string url, string profilePath)
    {
        var target = new TargetOptions(
            ConfigPaths: [],
            ServerNames: [],
            ProfilePaths: [profilePath],
            DisplayName: "anon-first-test",
            Url: new Uri(url, UriKind.Absolute),
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Command: null,
            CommandArguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            AuthOverrides: AuthOverrides.Empty);

        return new ParsedCommand(
            Command: AppCommand.Inspect,
            Subject: null,
            Arguments: null,
            Format: OutputFormat.Json,
            Timeout: HttpTimeout,
            Target: target,
            ProgressEnabled: false);
    }

    [Collection("HttpTestServer")]
    public sealed class AgainstAnonymousServer(HttpTestServerFixture fixture)
    {
        [Fact]
        public async Task ProfileLoaded_ButServerIsAnonymous_ConnectsWithoutCredentials()
        {
            // A profile is available, but the server allows anonymous access - so no token is sent.
            var profile = WriteProfile("unused", "this-token-must-not-be-sent");
            try
            {
                var outcome = await McpExecutor.ExecuteAsync(InspectWithProfile(fixture.BaseUrl, profile), JsonOptions, CancellationToken.None);

                outcome.HasErrors.ShouldBeFalse();
                var server = outcome.Payload.ShouldBeOfType<InspectReport>().Servers[0];
                server.Error.ShouldBeNull();
                server.Tools.Items.ShouldNotBeEmpty();

                var auth = server.AuthStatus.ShouldNotBeNull();
                auth.Mode.ShouldBe(ConnectionAuthModes.Anonymous);
            }
            finally
            {
                File.Delete(profile);
            }
        }
    }

    [Collection("BearerHttpTestServer")]
    public sealed class AgainstAuthRequiredServer(BearerHttpTestServerFixture fixture)
    {
        [Fact]
        public async Task AnonymousRefused_FallsBackToProfile_AndReportsIt()
        {
            // The server demands a bearer token: the anonymous attempt is refused (401), so we fall
            // back to the auto-picked profile and report that we authenticated via it.
            var profile = WriteProfile("fallback", BearerHttpTestServerFixture.TestToken);
            try
            {
                var outcome = await McpExecutor.ExecuteAsync(InspectWithProfile(fixture.BaseUrl, profile), JsonOptions, CancellationToken.None);

                outcome.HasErrors.ShouldBeFalse();
                var server = outcome.Payload.ShouldBeOfType<InspectReport>().Servers[0];
                server.Error.ShouldBeNull();
                server.Tools.Items.Select(tool => tool.Name).ShouldContain("Echo");

                var auth = server.AuthStatus.ShouldNotBeNull();
                auth.Mode.ShouldBe(ConnectionAuthModes.Authenticated);
                auth.Profile.ShouldBe("fallback");
                auth.Source.ShouldBe("auto-pick");
            }
            finally
            {
                File.Delete(profile);
            }
        }
    }
}
