using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpLense.Scanning.Checks;

/// <summary>
/// Classifies the target's authentication surface via the existing
/// <see cref="AuthScanner"/>. Output payload is the same shape as the v0.1
/// <c>auth-scan</c> command: classification, RFC 9728 details, profile attempts.
/// </summary>
/// <remarks>
/// This check loads profiles from <see cref="TargetOptions.ProfilePaths"/> + XDG defaults
/// and runs <see cref="AuthScanner.ScanCoreAsync"/> against the target. Other checks read
/// the classification out of <see cref="ScanContext.PriorOutputs"/> to decide how to open
/// their session (anonymous / which profile).
/// </remarks>
internal sealed class AuthCheck : IScanCheck
{
    public string Id => "auth";
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();
    public bool IsEnabledByDefault => true;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        // Stdio targets get a stub auth scan (classification: "stdio"); we still emit the
        // entry so downstream consumers can rely on `auth` being present for every server.
        if (context.Server.Kind != ConnectionKind.Http || context.Server.Url is null)
        {
            var stdioScan = new ServerAuthScan(
                Name: context.Server.Name,
                Transport: "stdio",
                Target: context.Server.Target,
                Classification: AuthClassifications.Stdio,
                Summary: "Stdio target - HTTP authentication does not apply.",
                Details: new AuthScanDetails(),
                ProfileAttempts: []);
            return new CheckOutcome(Ran: true, Data: ToNode(stdioScan), Error: null);
        }

        using var probe = new AuthProbe();
        var scanner = new AuthScanner(probe, new McpHandshakeProbe());

        // Profiles + auth overrides are first-class context properties (Tier 3.4 cleanup):
        // checks no longer dip into DI to find them.
        var profiles = context.Profiles;
        var overrides = context.AuthOverrides;
        var report = await scanner.ScanCoreAsync(
            new[] { context.Server },
            profiles,
            overrides,
            context.HandshakeTimeout,
            cancellationToken).ConfigureAwait(false);

        if (report.Servers.Count != 1)
        {
            return new CheckOutcome(Ran: true, Data: null, Error: "AuthScanner returned an unexpected number of server entries.");
        }

        var entry = report.Servers[0];

        // Publish the chosen auth path so downstream session-opening checks (tools, prompts,
        // resources, ...) all see the same credentials when they share the lazy session.
        if (string.Equals(entry.Classification, AuthClassifications.Anonymous, StringComparison.Ordinal))
        {
            context.PublishActiveAuth(null, "anonymous");
        }
        else
        {
            // Find the first successful profile and use its (substituted) auth.
            foreach (var attempt in entry.ProfileAttempts)
            {
                if (attempt.Success)
                {
                    var profile = profiles.FirstOrDefault(p => string.Equals(p.Name, attempt.ProfileName, StringComparison.OrdinalIgnoreCase));
                    if (profile is not null)
                    {
                        var auth = profile.Auth with { Scopes = attempt.Scopes };
                        context.PublishActiveAuth(auth, $"profile:{profile.Name}");
                        break;
                    }
                }
            }
        }

        return new CheckOutcome(Ran: true, Data: ToNode(entry), Error: null);
    }

    private static JsonNode? ToNode(object value) => JsonSerializer.SerializeToNode(value, SerializerOptions);

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
