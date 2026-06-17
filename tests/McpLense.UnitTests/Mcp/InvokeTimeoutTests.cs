using System;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Mcp;

public class InvokeTimeoutTests
{
    [Fact]
    public void DefaultTimeout_IsRaisedToTenMinuteFloor()
    {
        // The 30s default that bounds connect/list is far too short for a tool that forwards to a
        // slow backend, so invocations get at least a 10-minute window.
        McpExecutor.InvokeTimeout(TimeSpan.FromSeconds(30)).ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void ExplicitlyLargerTimeout_Wins()
    {
        // A bigger --timeout always takes precedence over the floor.
        McpExecutor.InvokeTimeout(TimeSpan.FromMinutes(30)).ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void TimeoutBelowFloor_IsLifted()
    {
        McpExecutor.InvokeTimeout(TimeSpan.FromMinutes(5)).ShouldBe(TimeSpan.FromMinutes(10));
    }
}
