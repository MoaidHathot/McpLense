using System.Net;
using System.Text;

namespace McpLense;

/// <summary>
/// Result of a successful authorization-code redirect.
/// </summary>
internal sealed record OAuthCallbackResult(string Code, string State);

/// <summary>
/// Abstraction over "bind a loopback HTTP listener and wait for the authorization-code redirect".
/// Implementations include the real <see cref="HttpListener"/>-based listener and a test double
/// that synthesises the callback in-process.
/// </summary>
internal interface IOAuthCallbackListener : IDisposable
{
    /// <summary>The redirect URI the listener is bound to (with the OS-assigned port resolved).</summary>
    Uri RedirectUri { get; }

    /// <summary>
    /// Awaits a single authorization-code redirect. Validates that the inbound <c>state</c>
    /// matches <paramref name="expectedState"/> and surfaces server-side errors via
    /// <see cref="McpLenseAuthException"/>.
    /// </summary>
    Task<OAuthCallbackResult> WaitForCallbackAsync(string expectedState, CancellationToken cancellationToken);
}

/// <summary>
/// Real listener that binds a <see cref="HttpListener"/> on <c>127.0.0.1:&lt;port&gt;</c> and
/// returns the first matching <c>?code=&amp;state=</c> redirect.
///
/// Caller chooses the port (<c>0</c> = OS-assigned). The listener returns a tiny HTML page so the
/// user's browser shows a friendly "you can close this tab" message instead of a raw response.
/// </summary>
internal sealed class HttpListenerCallback : IOAuthCallbackListener
{
    private const string SuccessHtml =
        "<!DOCTYPE html><html><head><meta charset='utf-8'><title>McpLense</title>" +
        "<style>body{font-family:sans-serif;background:#0d1117;color:#c9d1d9;display:flex;" +
        "align-items:center;justify-content:center;height:100vh;margin:0}div{text-align:center}" +
        "h1{color:#58a6ff}</style></head><body><div><h1>Authentication complete</h1>" +
        "<p>You can close this tab and return to McpLense.</p></div></body></html>";

    private const string ErrorHtmlFormat =
        "<!DOCTYPE html><html><head><meta charset='utf-8'><title>McpLense</title>" +
        "<style>body{{font-family:sans-serif;background:#0d1117;color:#c9d1d9;display:flex;" +
        "align-items:center;justify-content:center;height:100vh;margin:0}}div{{text-align:center}}" +
        "h1{{color:#f85149}}</style></head><body><div><h1>Authentication failed</h1>" +
        "<p>{0}</p></div></body></html>";

    private readonly HttpListener _listener;

    /// <inheritdoc />
    public Uri RedirectUri { get; }

    public HttpListenerCallback(string preferredRedirectUri)
    {
        ArgumentNullException.ThrowIfNull(preferredRedirectUri);

        var (prefix, redirect) = ResolvePrefix(preferredRedirectUri);

        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();

        RedirectUri = redirect;
    }

    /// <inheritdoc />
    public async Task<OAuthCallbackResult> WaitForCallbackAsync(string expectedState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedState);

        using var registration = cancellationToken.Register(() =>
        {
            try { _listener.Stop(); } catch { /* ignored */ }
        });

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (ObjectDisposedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new McpLenseAuthException("OAuth callback listener was closed before a redirect arrived.");
            }

            var query = context.Request.QueryString;
            var error = query["error"];
            var errorDescription = query["error_description"];
            var code = query["code"];
            var state = query["state"];

            if (!string.IsNullOrEmpty(error))
            {
                await WriteResponseAsync(context, HttpStatusCode.BadRequest,
                    string.Format(ErrorHtmlFormat, WebUtility.HtmlEncode($"{error}: {errorDescription ?? "(no description)"}")))
                    .ConfigureAwait(false);
                throw new McpLenseAuthException(
                    $"Authorization server returned error '{error}': {errorDescription ?? "(no description)"}");
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                // Some browsers (or extensions) probe the loopback listener with extra requests.
                // Politely 400 and keep listening.
                await WriteResponseAsync(context, HttpStatusCode.BadRequest,
                    string.Format(ErrorHtmlFormat, "Missing 'code' or 'state' parameter.")).ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                await WriteResponseAsync(context, HttpStatusCode.BadRequest,
                    string.Format(ErrorHtmlFormat, "State parameter mismatch.")).ConfigureAwait(false);
                throw new McpLenseAuthException("OAuth state parameter mismatch; potential CSRF.");
            }

            await WriteResponseAsync(context, HttpStatusCode.OK, SuccessHtml).ConfigureAwait(false);
            return new OAuthCallbackResult(code, state);
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* ignored */ }
        try { _listener.Close(); } catch { /* ignored */ }
    }

    private static async Task WriteResponseAsync(HttpListenerContext context, HttpStatusCode status, string body)
    {
        try
        {
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch
        {
            // ignored - best-effort response
        }
        finally
        {
            context.Response.Close();
        }
    }

    /// <summary>
    /// Normalises a redirect URI into the prefix form expected by <see cref="HttpListener"/>.
    /// When the supplied URI uses port 0, the listener picks a free port on <c>127.0.0.1</c>;
    /// the resulting URI (with the actual port) is returned via <see cref="RedirectUri"/>.
    /// </summary>
    private static (string Prefix, Uri Redirect) ResolvePrefix(string preferredRedirectUri)
    {
        if (!Uri.TryCreate(preferredRedirectUri, UriKind.Absolute, out var preferred))
        {
            throw new McpLenseAuthException($"Invalid redirect URI '{preferredRedirectUri}'.");
        }

        if (preferred.Scheme != Uri.UriSchemeHttp)
        {
            throw new McpLenseAuthException(
                $"Redirect URI '{preferredRedirectUri}' must use http:// (loopback). MCP OAuth profile mandates loopback redirects for native clients.");
        }

        var host = preferred.Host;
        var port = preferred.Port;
        var path = preferred.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            path = "/callback";
        }

        if (port == 0)
        {
            port = FindFreePort();
        }

        var prefix = $"http://{host}:{port}{(path.EndsWith('/') ? path : path + "/")}";
        var redirect = new Uri($"http://{host}:{port}{path}");
        return (prefix, redirect);
    }

    /// <summary>Finds a free TCP port by binding to port 0 with <see cref="System.Net.Sockets.TcpListener"/>.</summary>
    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
