using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace McpLense.IntegrationTests.Auth;

/// <summary>
/// In-process HTTP MCP test server that hosts a mock OAuth Identity Provider
/// (PRM/ASM/DCR/authorize/token) and gates every MCP request behind an IdP-issued bearer token.
/// </summary>
public sealed class OAuthHttpTestServerFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = string.Empty;

    public string PrmUrl => BaseUrl + ".well-known/oauth-protected-resource";
    public string AsmUrl => BaseUrl + ".well-known/oauth-authorization-server";
    public string RegisterUrl => BaseUrl + "oauth/register";
    public string AuthorizeUrl => BaseUrl + "oauth/authorize";
    public string TokenUrl => BaseUrl + "oauth/token";

    public async Task InitializeAsync()
    {
        _app = await McpLense.TestHttpServer.Program.StartAsync(urlFile: null, requireBearerToken: null, requireOAuth: true);
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
