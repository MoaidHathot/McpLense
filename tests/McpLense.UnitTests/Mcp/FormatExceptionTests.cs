using System;
using System.IO;
using System.Net;
using System.Net.Http;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Mcp;

public class FormatExceptionTests
{
    [Fact]
    public void Cancellation_MapsToTimeout_WithRaiseTimeoutHint()
    {
        var text = McpExecutor.FormatException(new OperationCanceledException());

        text.ShouldContain("Timed out");
        text.ShouldContain("--timeout");
    }

    [Fact]
    public void TransportDrop_AddsConnectionDropHint()
    {
        // No StatusCode = a real send/transport failure, not an HTTP status error.
        var ex = new HttpRequestException("An error occurred while sending the request.");

        var text = McpExecutor.FormatException(ex);

        text.ShouldContain("dropped mid-request");
        text.ShouldContain("request-timeout");
    }

    [Fact]
    public void IoException_IsTreatedAsConnectionDrop()
    {
        var text = McpExecutor.FormatException(new IOException("The response ended prematurely."));

        text.ShouldContain("dropped mid-request");
    }

    [Fact]
    public void HttpStatusError_IsNotAConnectionDrop()
    {
        // A status-bearing HttpRequestException (e.g. 401) must NOT get the connection-drop hint.
        var ex = new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).", null, HttpStatusCode.Unauthorized);

        var text = McpExecutor.FormatException(ex);

        text.ShouldContain("401");
        text.ShouldNotContain("dropped mid-request");
    }
}
