using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace McpLense.IntegrationTests;

/// <summary>
/// Boots the in-repo HTTP MCP test server in-process on an OS-assigned 127.0.0.1 port.
/// Exposes the Streamable HTTP base URL and the legacy SSE endpoint URL so tests can
/// exercise the McpExecutor HTTP code path against a real network listener without
/// needing a subprocess.
/// </summary>
public sealed class HttpTestServerFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = string.Empty;

    public string SseUrl => BaseUrl + "sse";

    public async Task InitializeAsync()
    {
        _app = await McpLense.TestHttpServer.Program.StartAsync();
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
