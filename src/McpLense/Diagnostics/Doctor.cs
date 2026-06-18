using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using McpLense.Analysis;

namespace McpLense;

internal enum DoctorStatus { Ok, Warn, Fail, Skip }

/// <summary>One diagnostic stage: what was tried, the verdict, a detail, and (on trouble) a hint.</summary>
internal sealed record DoctorStage(string Name, DoctorStatus Status, string Detail, string? Hint = null);

internal sealed record DoctorServerResult(string Name, string Target, string Transport, bool Ok, IReadOnlyList<DoctorStage> Stages);

internal sealed record DoctorReport(DateTimeOffset GeneratedAt, IReadOnlyList<DoctorServerResult> Servers);

/// <summary>
/// Staged connectivity triage for <c>mcplense doctor</c>: answers "why won't this MCP connect?" by
/// walking DNS -> TCP -> TLS -> MCP initialize -> auth and reporting exactly where it broke, with a
/// hint. Distinct from <c>scan</c> (an audit) - this is a developer's first-aid kit. Each stage is
/// captured; a failed prerequisite skips the stages that depend on it but never throws.
/// </summary>
internal static class DoctorRunner
{
    public static async Task<DoctorServerResult> RunAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stages = server.Kind == ConnectionKind.Http && server.Url is not null
            ? await RunHttpAsync(server, timeout, cancellationToken).ConfigureAwait(false)
            : await RunStdioAsync(server, timeout, cancellationToken).ConfigureAwait(false);

        var ok = stages.All(s => s.Status is DoctorStatus.Ok or DoctorStatus.Warn or DoctorStatus.Skip);
        return new DoctorServerResult(server.Name, server.Target, server.Kind == ConnectionKind.Http ? "http" : "stdio", ok, stages);
    }

    private static async Task<IReadOnlyList<DoctorStage>> RunHttpAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stages = new List<DoctorStage>();
        var url = server.Url!;
        var host = url.Host;
        var port = url.Port;
        var stageTimeout = timeout < TimeSpan.FromSeconds(10) ? timeout : TimeSpan.FromSeconds(10);

        // 1. DNS.
        IPAddress[] addresses;
        try
        {
            addresses = await WithTimeout(stageTimeout, cancellationToken, ct => Dns.GetHostAddressesAsync(host, ct)).ConfigureAwait(false);
            stages.Add(new DoctorStage("dns", addresses.Length > 0 ? DoctorStatus.Ok : DoctorStatus.Fail,
                addresses.Length > 0 ? $"{host} -> {string.Join(", ", addresses.Take(3).Select(a => a.ToString()))}" : "no addresses",
                addresses.Length > 0 ? null : "The hostname did not resolve - check for a typo or DNS/VPN issue."));
            if (addresses.Length == 0)
            {
                return Skip(stages, "tcp", "tls", "mcp-initialize", "auth");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            stages.Add(new DoctorStage("dns", DoctorStatus.Fail, Describe(ex), "The hostname did not resolve - check for a typo or DNS/VPN issue."));
            return Skip(stages, "tcp", "tls", "mcp-initialize", "auth");
        }

        // 2. TCP.
        try
        {
            using var tcp = new TcpClient();
            await WithTimeout(stageTimeout, cancellationToken, ct => tcp.ConnectAsync(host, port, ct).AsTask()).ConfigureAwait(false);
            stages.Add(new DoctorStage("tcp", DoctorStatus.Ok, $"connected to {host}:{port}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            stages.Add(new DoctorStage("tcp", DoctorStatus.Fail, Describe(ex), $"Could not open a TCP connection to {host}:{port} - check the port, a firewall, or whether the service is up."));
            return Skip(stages, "tls", "mcp-initialize", "auth");
        }

        // 3. TLS (https only).
        if (string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            stages.Add(await TlsStageAsync(host, port, stageTimeout, cancellationToken).ConfigureAwait(false));
        }
        else
        {
            stages.Add(new DoctorStage("tls", DoctorStatus.Warn, "target is http:// (no TLS)", "Serve the MCP over https - over http the Authorization header and traffic are unencrypted."));
        }

        // 4. MCP initialize (the definitive protocol-level test, unauthenticated).
        var handshake = await new McpHandshakeProbe().TryHandshakeAsync(server with { Auth = null }, timeout, cancellationToken).ConfigureAwait(false);
        stages.Add(handshake.Success
            ? new DoctorStage("mcp-initialize", DoctorStatus.Ok, $"handshake ok; {handshake.ToolCount ?? 0} tool(s) advertised")
            : new DoctorStage("mcp-initialize", DoctorStatus.Fail, handshake.Error ?? "failed", HandshakeHint(handshake.Error)));

        // 5. Auth classification (informational).
        stages.Add(await AuthStageAsync(server, timeout, cancellationToken).ConfigureAwait(false));

        return stages;
    }

    private static async Task<DoctorStage> TlsStageAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var tcp = new TcpClient();
            await WithTimeout(timeout, cancellationToken, ct => tcp.ConnectAsync(host, port, ct).AsTask()).ConfigureAwait(false);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await WithTimeout(timeout, cancellationToken, ct => ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct)).ConfigureAwait(false);

            var cert = ssl.RemoteCertificate is { } c ? new System.Security.Cryptography.X509Certificates.X509Certificate2(c) : null;
            if (cert is null)
            {
                return new DoctorStage("tls", DoctorStatus.Ok, $"TLS handshake ok ({ssl.SslProtocol})");
            }

            var days = (int)(cert.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
            var status = days < 0 ? DoctorStatus.Fail : days < 30 ? DoctorStatus.Warn : DoctorStatus.Ok;
            var hint = days < 0 ? "The certificate has expired - renew it." : days < 30 ? "The certificate expires soon - plan a renewal." : null;
            return new DoctorStage("tls", status, $"{ssl.SslProtocol}; cert '{cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)}' expires in {days} day(s)", hint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new DoctorStage("tls", DoctorStatus.Fail, Describe(ex), "TLS handshake failed - the certificate may be untrusted, expired, or the name may not match.");
        }
    }

    private static async Task<DoctorStage> AuthStageAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var probe = new AuthProbe();
            var outcome = await new AuthDiscovery(probe).ProbeAsync(server, cancellationToken).ConfigureAwait(false);
            if (outcome.ProbeError is not null || outcome.Result is null)
            {
                return new DoctorStage("auth", DoctorStatus.Warn, "could not classify auth", null);
            }

            var classification = AuthClassifier.ClassifyFromProbe(outcome.Result, AuthClassifier.BuildBaseDetails(outcome.Result));
            var label = classification?.Classification ?? "anonymous-or-unknown";
            return new DoctorStage("auth", DoctorStatus.Ok, $"classification: {label}",
                classification?.Classification is "oauth-rfc9728" or "oauth-bearer-unannounced" or "auth-required-unspecified"
                    ? "This server requires auth - pass --profile <name> (or --auth bearer --auth-token ...)."
                    : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new DoctorStage("auth", DoctorStatus.Warn, Describe(ex));
        }
    }

    private static async Task<IReadOnlyList<DoctorStage>> RunStdioAsync(ResolvedServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stages = new List<DoctorStage>
        {
            new("command", DoctorStatus.Ok, $"{server.Command} {string.Join(' ', server.CommandArguments)}".Trim())
        };

        var handshake = await McpExecutor.TryStdioHandshakeAsync(server, timeout, cancellationToken).ConfigureAwait(false);
        stages.Add(handshake.Success
            ? new DoctorStage("mcp-initialize", DoctorStatus.Ok, $"handshake ok; {handshake.ToolCount ?? 0} tool(s) advertised")
            : new DoctorStage("mcp-initialize", DoctorStatus.Fail, handshake.Error ?? "failed",
                "The stdio server failed to start or complete the handshake - check the command, args, cwd, and env, and run it manually to see its output."));
        return stages;
    }

    private static string? HandshakeHint(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return null;
        }

        if (error.Contains("401") || error.Contains("403") || error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "The server requires authentication - pass --profile <name> or --auth bearer --auth-token ...";
        }

        if (error.Contains("405") || error.Contains("406") || error.Contains("session", StringComparison.OrdinalIgnoreCase))
        {
            return "The transport may be mismatched - try --transport sse or --transport streamable-http.";
        }

        return "The MCP initialize handshake failed - the endpoint may not be an MCP server, or the transport may differ.";
    }

    private static IReadOnlyList<DoctorStage> Skip(List<DoctorStage> stages, params string[] names)
    {
        foreach (var name in names)
        {
            stages.Add(new DoctorStage(name, DoctorStatus.Skip, "skipped (a previous stage failed)"));
        }

        return stages;
    }

    private static async Task<T> WithTimeout<T>(TimeSpan timeout, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return await action(cts.Token).ConfigureAwait(false);
    }

    private static async Task WithTimeout(TimeSpan timeout, CancellationToken cancellationToken, Func<CancellationToken, Task> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        await action(cts.Token).ConfigureAwait(false);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
