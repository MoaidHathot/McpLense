using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace McpLense.Scanning.Checks;

/// <summary>
/// Reads the TLS leaf certificate + chain. Reuses the transport probe's captured cert (no
/// extra network call). When the chain isn't available from the probe, the check opens a
/// fresh socket-level TLS handshake to capture it - same cost as the existing transport
/// probe.
/// </summary>
internal sealed class TlsChainCheck : IScanCheck
{
    public string Id => "tlsChain";
    public IReadOnlyList<string> DependsOn => new[] { "transport" };
    public bool IsEnabledByDefault => true;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (context.Server.Kind != ConnectionKind.Http || context.Server.Url is null)
        {
            return CheckOutcome.Skipped;
        }

        if (!string.Equals(context.Server.Url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            // Plain HTTP - no TLS to inspect.
            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new TlsChainData(false, "Target is not HTTPS.", [], null, null)), Error: null);
        }

        // Open a dedicated TLS connection to inspect the chain. We can't easily extract the
        // full chain from HttpClient's recorded cert (only the leaf), so we do one socket-
        // level handshake here. Slightly more code, dramatically more information than the
        // leaf alone gives.
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var port = context.Server.Url.IsDefaultPort ? 443 : context.Server.Url.Port;
            await tcp.ConnectAsync(context.Server.Url.Host, port, cancellationToken).ConfigureAwait(false);

            X509Chain? capturedChain = null;
            SslPolicyErrors? capturedErrors = null;

            using var network = tcp.GetStream();
            using var ssl = new SslStream(network, leaveInnerStreamOpen: false, (sender, cert, chain, errors) =>
            {
                if (chain is not null)
                {
                    capturedChain = chain;
                }

                capturedErrors = errors;
                return true; // we already trust the transport probe's verdict; we just want the bytes
            });

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = context.Server.Url.Host,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
            }, cancellationToken).ConfigureAwait(false);

            if (capturedChain is null)
            {
                return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new TlsChainData(false, "Chain not captured.", [], null, null)), Error: null);
            }

            var intermediates = capturedChain.ChainElements
                .Cast<X509ChainElement>()
                .Skip(1)
                .Select(e => new ChainEntry(
                    Subject: e.Certificate.Subject,
                    Issuer: e.Certificate.Issuer,
                    SubjectAlternativeNames: GetSans(e.Certificate),
                    NotBefore: new DateTimeOffset(e.Certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
                    NotAfter: new DateTimeOffset(e.Certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                    Thumbprint: e.Certificate.Thumbprint,
                    SignatureAlgorithm: e.Certificate.SignatureAlgorithm?.FriendlyName))
                .ToArray();

            var policyErrors = capturedChain.ChainStatus
                .Select(s => s.StatusInformation?.Trim() ?? s.Status.ToString())
                .ToArray();

            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new TlsChainData(
                Captured: true,
                FailureReason: null,
                Intermediates: intermediates,
                ChainValid: capturedErrors == SslPolicyErrors.None && policyErrors.Length == 0,
                ChainPolicyErrors: policyErrors)), Error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new TlsChainData(false, $"{ex.GetType().Name}: {ex.Message}", [], null, null)), Error: null);
        }
    }

    private static IReadOnlyList<string> GetSans(X509Certificate2 cert)
    {
        var ext = cert.Extensions["2.5.29.17"];
        if (ext is null)
        {
            return [];
        }

        return ext.Format(multiLine: false)
            .Split([","], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    internal sealed record TlsChainData(
        bool Captured,
        string? FailureReason,
        IReadOnlyList<ChainEntry> Intermediates,
        bool? ChainValid,
        IReadOnlyList<string>? ChainPolicyErrors);

    internal sealed record ChainEntry(
        string Subject,
        string Issuer,
        IReadOnlyList<string> SubjectAlternativeNames,
        DateTimeOffset NotBefore,
        DateTimeOffset NotAfter,
        string Thumbprint,
        string? SignatureAlgorithm);
}
