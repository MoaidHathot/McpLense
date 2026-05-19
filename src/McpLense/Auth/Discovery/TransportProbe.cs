using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using McpLense.Scanning.TargetResolution;

namespace McpLense;

/// <summary>
/// Outcome of <see cref="TransportProbe.ProbeAsync"/>: status, headers, TLS leaf-cert details
/// (when HTTPS), and the negotiated TLS protocol version when we could capture it. All fields
/// are nullable / empty when the probe failed or the data wasn't available; callers report
/// the facts they have rather than synthesising guesses.
/// </summary>
internal sealed record TransportProbeResult(
    int? StatusCode = null,
    bool Reached = false,
    string? Error = null,
    ResponseHeadersSummary? Headers = null,
    TlsInfo? Tls = null);

internal interface ITransportProbe
{
    Task<TransportProbeResult> ProbeAsync(Uri serverUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Same-origin probe with optional <paramref name="additionalHeaders"/> attached to the
    /// outbound GET. Headers are honoured only when <paramref name="scope"/> is
    /// <see cref="TargetScope.All"/>; <see cref="TargetScope.Session"/> reverts to the bare
    /// behaviour of <see cref="ProbeAsync(Uri, CancellationToken)"/>.
    /// </summary>
    Task<TransportProbeResult> ProbeAsync(
        Uri serverUrl,
        IReadOnlyDictionary<string, string>? additionalHeaders,
        TargetScope scope,
        CancellationToken cancellationToken);
}

/// <summary>
/// Issues a single unauthenticated <c>GET</c> against the target and captures:
/// <list type="bullet">
///   <item>HTTP status code,</item>
///   <item>every security-relevant response header (HSTS, CSP, Server, CORS, ...),</item>
///   <item>the leaf TLS certificate from the connection (subject, issuer, validity, SANs, ...)
///         via <see cref="SslClientAuthenticationOptions.RemoteCertificateValidationCallback"/>,</item>
///   <item>the negotiated TLS protocol version, when the platform exposes it.</item>
/// </list>
/// We deliberately keep this separate from <see cref="AuthProbe"/> so the audit command doesn't
/// share state with the auth resolver's caching scheme - the transport probe is only ever
/// called once per server per audit run, while the auth probe is memoised across calls.
/// </summary>
internal sealed class TransportProbe : ITransportProbe, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly SocketsHttpHandler _handler;
    private readonly bool _ownsResources;

    // The cert + protocol version is captured during the TLS handshake by a per-request
    // callback. We thread the captured values back through an AsyncLocal so the GET response
    // doesn't have to carry them out-of-band, and so concurrent probes don't trample each
    // other's data.
    private static readonly AsyncLocal<CaptureSlot?> _captureSlot = new();

    public TransportProbe()
    {
        _handler = new SocketsHttpHandler
        {
            // Replace the SslOptions callback per-request so we can pin the captured cert
            // into the AsyncLocal slot without polluting other consumers.
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = CaptureCallback
            },
            // Disable AutomaticDecompression: we don't read the body and don't want to spend
            // any CPU on this; the audit cares about headers and the leaf cert only.
            AutomaticDecompression = System.Net.DecompressionMethods.None
        };

        _httpClient = new HttpClient(_handler)
        {
            Timeout = DefaultTimeout
        };
        _ownsResources = true;
    }

    /// <summary>For tests: bring your own HttpClient (cert capture won't fire when the handler
    /// is not <see cref="SocketsHttpHandler"/>, which is fine - tests inject HTTP-only fixtures).</summary>
    internal TransportProbe(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _handler = null!;
        _ownsResources = false;
    }

    public Task<TransportProbeResult> ProbeAsync(Uri serverUrl, CancellationToken cancellationToken)
        => ProbeAsync(serverUrl, additionalHeaders: null, scope: TargetScope.All, cancellationToken);

    public async Task<TransportProbeResult> ProbeAsync(
        Uri serverUrl,
        IReadOnlyDictionary<string, string>? additionalHeaders,
        TargetScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);

        // Per-request capture slot - the SslOptions callback writes into it during the TLS
        // handshake. We always clear it afterwards so the slot doesn't outlive the call.
        var slot = new CaptureSlot();
        _captureSlot.Value = slot;

        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, serverUrl);
            request.Headers.Authorization = null;

            // Per-target headers (e.g. x-mcp-ec-organization) are honoured here when the
            // overlay declares scope=all. Scope=session keeps the probe bare so the user
            // can still observe how an UNauthenticated request to the server behaves.
            // Use TryAddWithoutValidation so the probe never rejects a non-canonical
            // header name the user supplied.
            if (scope == TargetScope.All && additionalHeaders is { Count: > 0 })
            {
                foreach (var (name, value) in additionalHeaders)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            var headers = SnapshotHeaders(response);
            var tls = ToTlsInfo(slot.Certificate, slot.ProtocolVersion);

            return new TransportProbeResult(
                StatusCode: (int)response.StatusCode,
                Reached: true,
                Headers: headers,
                Tls: tls);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network failure / DNS / TLS handshake failure: still report whatever cert details
            // we captured before the failure (e.g. expired cert => handshake fails AFTER
            // certificate callback ran, so we DO have the cert; consumers can see "fetch
            // failed with this cert presented").
            var tls = ToTlsInfo(slot.Certificate, slot.ProtocolVersion);
            return new TransportProbeResult(
                Reached: false,
                Error: $"{ex.GetType().Name}: {ex.Message}",
                Tls: tls);
        }
        finally
        {
            response?.Dispose();
            _captureSlot.Value = null;
        }
    }

    private static bool CaptureCallback(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        // Capture the leaf certificate for the audit, regardless of validation outcome - the
        // user cares about "what cert did the server present", not "is the cert acceptable to
        // this OS trust store". We still TRUST the platform's validation result so we don't
        // accidentally turn the probe into a security hole; only the bytes are captured.
        var slot = _captureSlot.Value;
        if (slot is null)
        {
            return sslPolicyErrors == SslPolicyErrors.None;
        }

        // .NET hands us X509Certificate; upgrade to X509Certificate2 for full property access.
        // X509Certificate2 constructor accepts a base X509Certificate.
        if (certificate is X509Certificate2 cert2)
        {
            slot.Certificate = cert2;
        }
        else if (certificate is not null)
        {
            slot.Certificate = new X509Certificate2(certificate);
        }

        if (sender is SslStream stream)
        {
            slot.ProtocolVersion = stream.SslProtocol.ToString();
        }

        return sslPolicyErrors == SslPolicyErrors.None;
    }

    private static TlsInfo? ToTlsInfo(X509Certificate2? cert, string? protocolVersion)
    {
        if (cert is null)
        {
            return string.IsNullOrEmpty(protocolVersion)
                ? null
                : new TlsInfo(null, null, null, null, null, null, null, null, [], protocolVersion);
        }

        var notBefore = new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        var notAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var daysUntilExpiry = (int)Math.Floor((notAfter - DateTimeOffset.UtcNow).TotalDays);

        // Subject Alternative Names live in the SAN extension (OID 2.5.29.17). We pull them
        // because the practical "is this cert valid for this URL" check is "is the URL host
        // in the SAN list", which the user may want to verify themselves.
        var sanExtension = cert.Extensions["2.5.29.17"];
        var sans = sanExtension is null
            ? Array.Empty<string>()
            : ParseSubjectAlternativeNames(sanExtension);

        return new TlsInfo(
            Subject: cert.Subject,
            Issuer: cert.Issuer,
            Thumbprint: cert.Thumbprint,
            SerialNumber: cert.SerialNumber,
            NotBefore: notBefore,
            NotAfter: notAfter,
            DaysUntilExpiry: daysUntilExpiry,
            SignatureAlgorithm: cert.SignatureAlgorithm?.FriendlyName,
            SubjectAlternativeNames: sans,
            ProtocolVersion: protocolVersion);
    }

    private static IReadOnlyList<string> ParseSubjectAlternativeNames(X509Extension extension)
    {
        // X509Extension.Format(false) produces a human-readable but locale-dependent string
        // (e.g. "DNS Name=foo.com, DNS Name=bar.com" on en-US, "DNS-Name=foo.com" elsewhere).
        // For machine-readable output we'd parse the ASN.1 bytes; for the audit, the formatted
        // string is enough - we surface the raw entries verbatim so consumers parse downstream.
        var formatted = extension.Format(multiLine: false);
        if (string.IsNullOrEmpty(formatted))
        {
            return [];
        }

        return formatted
            .Split([","], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry)
            .ToArray();
    }

    private static ResponseHeadersSummary SnapshotHeaders(HttpResponseMessage response)
    {
        string? Get(string name)
            => response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values)
               : response.Content.Headers.TryGetValues(name, out var contentValues) ? string.Join(", ", contentValues)
               : null;

        // Snapshot every other header verbatim into the Other dictionary; the named fields
        // get a typed home for ergonomics, but we don't drop any header the server returned -
        // some servers convey security policy via custom headers (`X-Permitted-Cross-Domain-
        // Policies`, `Expect-CT`, etc.) that the user may want to inspect.
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Server", "X-Powered-By",
            "Strict-Transport-Security", "Content-Security-Policy",
            "X-Frame-Options", "X-Content-Type-Options", "Referrer-Policy",
            "Access-Control-Allow-Origin", "Access-Control-Allow-Credentials",
            "Cache-Control"
        };

        var other = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (named.Contains(header.Key))
            {
                continue;
            }

            other[header.Key] = string.Join(", ", header.Value);
        }

        return new ResponseHeadersSummary(
            Server: Get("Server"),
            XPoweredBy: Get("X-Powered-By"),
            StrictTransportSecurity: Get("Strict-Transport-Security"),
            ContentSecurityPolicy: Get("Content-Security-Policy"),
            XFrameOptions: Get("X-Frame-Options"),
            XContentTypeOptions: Get("X-Content-Type-Options"),
            ReferrerPolicy: Get("Referrer-Policy"),
            AccessControlAllowOrigin: Get("Access-Control-Allow-Origin"),
            AccessControlAllowCredentials: Get("Access-Control-Allow-Credentials"),
            CacheControl: Get("Cache-Control"),
            Other: other);
    }

    public void Dispose()
    {
        if (_ownsResources)
        {
            _httpClient.Dispose();
            _handler?.Dispose();
        }
    }

    private sealed class CaptureSlot
    {
        public X509Certificate2? Certificate;
        public string? ProtocolVersion;
    }
}
