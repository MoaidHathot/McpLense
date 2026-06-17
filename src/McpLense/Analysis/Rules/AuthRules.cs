using McpLense.Scanning;

namespace McpLense.Analysis.Rules;

/// <summary>
/// An anonymous server (no credentials required) that exposes a tool the server itself marks
/// destructive or open-world means anyone who can reach the endpoint can invoke a high-impact tool.
/// </summary>
public sealed class AnonymousDestructiveRule : IFindingRule
{
    public string Id => "anonymous-destructive";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        var classification = facts.Check("auth").Str("classification");
        if (!string.Equals(classification, "anonymous", StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (var tool in facts.ToolItems())
        {
            var name = tool.Str("name") ?? "(unnamed)";
            var annotations = tool["annotations"];
            var destructive = annotations.Bool("destructiveHint") == true;
            var openWorld = annotations.Bool("openWorldHint") == true;
            if (destructive || openWorld)
            {
                var which = destructive ? "destructive" : "open-world";
                yield return new Finding(
                    Id,
                    Severity.High,
                    $"Anonymous server exposes a {which} tool '{name}'",
                    $"checks.tools.items[name={name}].annotations",
                    destructive ? "destructiveHint=true" : "openWorldHint=true",
                    "Require authentication for this server, or gate the high-impact tool behind auth, so an unauthenticated caller cannot invoke it.");
            }
        }
    }
}

/// <summary>
/// A server that demands Bearer auth but advertises no RFC 9728 protected-resource metadata gives
/// clients no machine-discoverable way to learn how to authenticate.
/// </summary>
public sealed class UnannouncedBearerRule : IFindingRule
{
    public string Id => "unannounced-bearer";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        var classification = facts.Check("auth").Str("classification");
        if (string.Equals(classification, "oauth-bearer-unannounced", StringComparison.Ordinal))
        {
            yield return new Finding(
                Id,
                Severity.Low,
                "Server requires Bearer auth but does not advertise RFC 9728 metadata",
                "checks.auth.classification",
                "oauth-bearer-unannounced",
                "Publish an RFC 9728 protected-resource-metadata document so clients can discover the authorization server and scopes.");
        }
    }
}
