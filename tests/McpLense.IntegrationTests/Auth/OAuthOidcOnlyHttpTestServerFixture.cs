using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace McpLense.IntegrationTests.Auth;

/// <summary>
/// Variant of <see cref="OAuthHttpTestServerFixture"/> that simulates an OIDC-only authorization
/// server (Microsoft Entra ID v2.0 shape): the RFC 8414 <c>oauth-authorization-server</c> well-known
/// returns 404, and the same metadata is exposed at <c>/.well-known/openid-configuration</c>
/// instead. Used to verify the discovery fallback ladder end-to-end.
/// </summary>
public sealed class OAuthOidcOnlyHttpTestServerFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = string.Empty;

    public string PrmUrl => BaseUrl + ".well-known/oauth-protected-resource";
    public string AsmUrl => BaseUrl + ".well-known/oauth-authorization-server";
    public string OidcUrl => BaseUrl + ".well-known/openid-configuration";
    public string RegisterUrl => BaseUrl + "oauth/register";
    public string AuthorizeUrl => BaseUrl + "oauth/authorize";
    public string TokenUrl => BaseUrl + "oauth/token";

    public async Task InitializeAsync()
    {
        _app = await McpLense.TestHttpServer.Program.StartAsync(
            urlFile: null,
            requireBearerToken: null,
            requireOAuth: true,
            oidcOnly: true);
        var url = _app.Urls.First();
        BaseUrl = url.EndsWith('/') ? url : url + "/";
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(10));
            await _app.StopAsync(cts.Token);
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
