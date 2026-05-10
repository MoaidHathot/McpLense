using System.Net.Http.Headers;

namespace McpLense;

/// <summary>
/// <see cref="DelegatingHandler"/> that injects a static <c>Authorization: Bearer &lt;token&gt;</c>
/// header on every outbound HTTP request. Always overwrites any pre-existing
/// <c>Authorization</c> header so the configured token wins (and so subsequent retries
/// do not pick up a stale value).
/// </summary>
internal sealed class BearerHandler : DelegatingHandler
{
    private readonly string _token;

    public BearerHandler(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("Bearer token cannot be null or empty.", nameof(token));
        }

        _token = token;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return base.SendAsync(request, cancellationToken);
    }
}
