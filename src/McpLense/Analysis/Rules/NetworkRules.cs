using McpLense.Scanning;

namespace McpLense.Analysis.Rules;

/// <summary>A wildcard CORS origin combined with credentials lets any website make
/// credentialed cross-origin requests to the server - a classic high-risk misconfiguration.</summary>
public sealed class WeakCorsRule : IFindingRule
{
    public string Id => "weak-cors";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        var cors = facts.Check("corsPreflight");
        var origin = cors.Str("accessControlAllowOrigin");
        var credentials = cors.Str("accessControlAllowCredentials");
        if (origin == "*" && string.Equals(credentials, "true", StringComparison.OrdinalIgnoreCase))
        {
            yield return new Finding(
                Id,
                Severity.High,
                "Wildcard CORS origin combined with credentials",
                "checks.corsPreflight",
                "access-control-allow-origin: * with access-control-allow-credentials: true",
                "Do not return Access-Control-Allow-Origin:* together with Access-Control-Allow-Credentials:true; echo a specific allow-listed origin instead.");
        }
    }
}

/// <summary>The leaf TLS certificate is expired or close to expiry.</summary>
public sealed class TlsExpiryRule : IFindingRule
{
    public string Id => "tls-expiry";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        var days = facts.Check("transport")?["tls"].Number("daysUntilExpiry");
        if (days is null)
        {
            yield break;
        }

        if (days < 0)
        {
            yield return new Finding(
                Id,
                Severity.Critical,
                $"TLS certificate is expired ({(int)days} days)",
                "checks.transport.tls.daysUntilExpiry",
                ((int)days).ToString(),
                "Renew the TLS certificate - it has already expired and clients will refuse the connection.");
        }
        else if (days < 30)
        {
            yield return new Finding(
                Id,
                Severity.Medium,
                $"TLS certificate expires soon ({(int)days} days)",
                "checks.transport.tls.daysUntilExpiry",
                ((int)days).ToString(),
                "Renew the TLS certificate before it expires.");
        }
    }
}

/// <summary>The target is served over plain HTTP - tokens and traffic are unencrypted.</summary>
public sealed class MixedContentRule : IFindingRule
{
    public string Id => "mixed-content";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        if (facts.Check("transport").Bool("mixedContent") == true)
        {
            yield return new Finding(
                Id,
                Severity.High,
                "Server is reachable over plain HTTP",
                "checks.transport.mixedContent",
                "true",
                "Serve the MCP endpoint over HTTPS only - over HTTP the Authorization header and all traffic are sent in the clear.");
        }
    }
}

/// <summary>OS-level TLS chain validation failed (untrusted / broken certificate chain).</summary>
public sealed class TlsChainInvalidRule : IFindingRule
{
    public string Id => "tls-chain-invalid";
    public bool DefaultEnabled => true;

    public IEnumerable<Finding> Evaluate(ServerScanResult facts)
    {
        var chain = facts.Check("tlsChain");
        if (chain.Bool("chainValid") == false)
        {
            var errors = (chain.Array("chainPolicyErrors"))?.Select(e => e.AsStr()).Where(e => e is not null) ?? [];
            yield return new Finding(
                Id,
                Severity.High,
                "TLS certificate chain failed validation",
                "checks.tlsChain.chainPolicyErrors",
                string.Join("; ", errors),
                "Fix the certificate chain (missing intermediates / untrusted root / name mismatch) so clients can validate it.");
        }
    }
}
