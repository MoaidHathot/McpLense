using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth.Discovery;

/// <summary>
/// Exercises <see cref="HttpListenerCallback"/> by binding a real loopback listener on an
/// OS-assigned port and driving callback redirects with a plain <see cref="HttpClient"/>.
/// </summary>
public class HttpListenerCallbackTests
{
    [Fact]
    public void Constructor_NonHttpScheme_Throws()
    {
        var ex = Should.Throw<McpLenseAuthException>(() =>
            new HttpListenerCallback("https://127.0.0.1:5050/callback"));
        ex.Message.ShouldContain("loopback");
    }

    [Fact]
    public void Constructor_MalformedUri_Throws()
    {
        var ex = Should.Throw<McpLenseAuthException>(() =>
            new HttpListenerCallback("not a uri"));
        ex.Message.ShouldContain("Invalid redirect URI");
    }

    [Fact]
    public void Constructor_PortZero_AssignsRealPort()
    {
        using var listener = new HttpListenerCallback("http://127.0.0.1:0/callback");

        listener.RedirectUri.Scheme.ShouldBe(Uri.UriSchemeHttp);
        listener.RedirectUri.Host.ShouldBe("127.0.0.1");
        listener.RedirectUri.Port.ShouldBeGreaterThan(0);
        listener.RedirectUri.AbsolutePath.ShouldBe("/callback");
    }

    [Fact]
    public void Constructor_RootPath_DefaultsToCallback()
    {
        using var listener = new HttpListenerCallback("http://127.0.0.1:0/");

        listener.RedirectUri.AbsolutePath.ShouldBe("/callback");
    }

    [Fact]
    public async Task WaitForCallback_HappyPath_ReturnsCodeAndState()
    {
        using var listener = new HttpListenerCallback("http://127.0.0.1:0/callback");

        var waitTask = listener.WaitForCallbackAsync("state-xyz", CancellationToken.None);

        using var http = new HttpClient();
        using var response = await http.GetAsync(
            new Uri($"{listener.RedirectUri}?code=auth-code-123&state=state-xyz"));

        response.IsSuccessStatusCode.ShouldBeTrue();
        var result = await waitTask;

        result.Code.ShouldBe("auth-code-123");
        result.State.ShouldBe("state-xyz");
    }

    [Fact]
    public async Task WaitForCallback_StateMismatch_Throws()
    {
        using var listener = new HttpListenerCallback("http://127.0.0.1:0/callback");

        var waitTask = listener.WaitForCallbackAsync("expected-state", CancellationToken.None);

        using var http = new HttpClient();
        using var response = await http.GetAsync(
            new Uri($"{listener.RedirectUri}?code=abc&state=wrong-state"));

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() => waitTask);
        ex.Message.ShouldContain("state parameter mismatch");
    }

    [Fact]
    public async Task WaitForCallback_ErrorParameter_Throws()
    {
        using var listener = new HttpListenerCallback("http://127.0.0.1:0/callback");

        var waitTask = listener.WaitForCallbackAsync("any", CancellationToken.None);

        using var http = new HttpClient();
        using var response = await http.GetAsync(
            new Uri($"{listener.RedirectUri}?error=access_denied&error_description=user+rejected"));

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);

        var ex = await Should.ThrowAsync<McpLenseAuthException>(() => waitTask);
        ex.Message.ShouldContain("access_denied");
        ex.Message.ShouldContain("user rejected");
    }

    [Fact]
    public async Task WaitForCallback_MissingCode_KeepsListeningUntilValidRedirect()
    {
        using var listener = new HttpListenerCallback("http://127.0.0.1:0/callback");

        var waitTask = listener.WaitForCallbackAsync("state-1", CancellationToken.None);

        using var http = new HttpClient();
        // First, a probe with no code/state — listener should respond 400 and keep listening.
        using (var probe = await http.GetAsync(new Uri($"{listener.RedirectUri}?foo=bar")))
        {
            probe.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        }

        // Then deliver the real redirect.
        using var ok = await http.GetAsync(
            new Uri($"{listener.RedirectUri}?code=real&state=state-1"));
        ok.IsSuccessStatusCode.ShouldBeTrue();

        var result = await waitTask;
        result.Code.ShouldBe("real");
        result.State.ShouldBe("state-1");
    }

    [Fact]
    public async Task WaitForCallback_Cancelled_ThrowsOperationCanceled()
    {
        using var listener = new HttpListenerCallback("http://127.0.0.1:0/callback");
        using var cts = new CancellationTokenSource();

        var waitTask = listener.WaitForCallbackAsync("any", cts.Token);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => waitTask);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var listener = new HttpListenerCallback("http://127.0.0.1:0/callback");

        Should.NotThrow(() => listener.Dispose());
        Should.NotThrow(() => listener.Dispose()); // safe to call twice
    }

    [Fact]
    public async Task Dispose_AfterDispose_RedirectIsUnreachable()
    {
        var listener = new HttpListenerCallback("http://127.0.0.1:0/callback");
        var redirect = listener.RedirectUri;
        listener.Dispose();

        // After disposal the listener should no longer service the redirect URI; we accept any
        // network-style failure (refused, reset, or timeout) as proof the port is closed.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var ex = await Should.ThrowAsync<Exception>(() => http.GetAsync(redirect));
        (ex is HttpRequestException || ex is TaskCanceledException).ShouldBeTrue(
            $"Expected HttpRequestException or TaskCanceledException but got {ex.GetType().Name}");
    }
}
