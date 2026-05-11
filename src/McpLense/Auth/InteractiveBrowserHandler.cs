using Azure.Core;

namespace McpLense;

/// <summary>
/// <see cref="DelegatingHandler"/> that asks an <see cref="Azure.Core.TokenCredential"/> for an
/// access token on every outbound HTTP request and stamps it onto
/// <c>Authorization: Bearer &lt;token&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TokenCredential.GetTokenAsync(TokenRequestContext, CancellationToken)"/> is the
/// MSAL-cache-aware fast path: it returns the cached token immediately when it is still valid,
/// triggers a silent refresh when it isn't, and only escalates to the interactive flow when the
/// refresh fails. We always call it (instead of caching the <see cref="AccessToken"/> ourselves)
/// so the underlying MSAL token cache stays the single source of truth.
/// </para>
/// <para>
/// Note: this handler is deliberately ignorant of the <see cref="ResolvedAuth"/> shape. Composition
/// of <see cref="InteractiveBrowserCredentialOptions"/> happens in
/// <see cref="AuthHandlerFactory.CreateInteractiveBrowser"/>, keeping this class easy to unit-test
/// against a fake <see cref="TokenCredential"/>.
/// </para>
/// </remarks>
internal sealed class InteractiveBrowserHandler : DelegatingHandler
{
    private readonly TokenCredential _credential;
    private readonly string[] _scopes;

    public InteractiveBrowserHandler(TokenCredential credential, IReadOnlyList<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(scopes);

        if (scopes.Count == 0)
        {
            throw new ArgumentException("At least one scope is required.", nameof(scopes));
        }

        _credential = credential;
        _scopes = scopes.ToArray();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = new TokenRequestContext(_scopes);
        AccessToken token;
        try
        {
            token = await _credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpLenseAuthException(
                "Failed to acquire an access token via interactive-browser auth. " +
                "Check that the configured clientId/tenantId/scopes match your Entra app registration. " +
                $"Underlying error: {ex.GetType().Name}: {ex.Message}",
                ex);
        }

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
