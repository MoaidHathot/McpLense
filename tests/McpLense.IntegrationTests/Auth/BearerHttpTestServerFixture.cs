using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace McpLense.IntegrationTests.Auth;

/// <summary>
/// In-process HTTP MCP test server that requires <c>Authorization: Bearer &lt;TestToken&gt;</c>
/// on every request to <c>/mcp</c> and <c>/sse</c>. Used by bearer auth integration tests.
/// </summary>
public sealed class BearerHttpTestServerFixture : IAsyncLifetime
{
    public const string TestToken = "integration-bearer-token-abc123";

    private WebApplication? _app;

    public string BaseUrl { get; private set; } = string.Empty;

    public string SseUrl => BaseUrl + "sse";

    public async Task InitializeAsync()
    {
        _app = await McpLense.TestHttpServer.Program.StartAsync(urlFile: null, requireBearerToken: TestToken);
        var url = _app.Urls.First();
        BaseUrl = url.EndsWith('/') ? url : url + "/";
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.StopAsync(cts.Token);
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
