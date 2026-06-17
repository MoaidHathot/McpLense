using McpLense;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Tui;

/// <summary>
/// Unit-tests the TUI server-initiated interaction in isolation: it must advertise the three
/// client capabilities, answer with safe defaults, and capture every request/notification so the
/// TUI can show it. Drain is one-shot (clears what it returns).
/// </summary>
public class TuiServerInteractionTests
{
    [Fact]
    public void Capabilities_AdvertiseSamplingElicitationRoots()
    {
        var interaction = new TuiServerInteraction();

        interaction.Capabilities.Sampling.ShouldNotBeNull();
        interaction.Capabilities.Elicitation.ShouldNotBeNull();
        interaction.Capabilities.Roots.ShouldNotBeNull();
    }

    [Fact]
    public async Task Elicit_DeclinesAndCaptures()
    {
        var interaction = new TuiServerInteraction();

        var result = await interaction.ElicitAsync(new ElicitRequestParams { Message = "Pick one" }, CancellationToken.None);

        result.Action.ShouldBe("decline");
        var captured = interaction.Drain();
        captured.Count.ShouldBe(1);
        captured[0].Method.ShouldBe("elicitation/create");
        captured[0].Detail.ShouldBe("Pick one");
        captured[0].Response.ShouldBe("declined");
    }

    [Fact]
    public async Task ListRoots_ReturnsEmptyAndCaptures()
    {
        var interaction = new TuiServerInteraction();

        var result = await interaction.ListRootsAsync(new ListRootsRequestParams(), CancellationToken.None);

        result.Roots.ShouldBeEmpty();
        interaction.Drain().ShouldHaveSingleItem().Method.ShouldBe("roots/list");
    }

    [Fact]
    public async Task CreateMessage_RefusesButStillCaptures()
    {
        var interaction = new TuiServerInteraction();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await interaction.CreateMessageAsync(null, new Progress<ProgressNotificationValue>(), CancellationToken.None));

        var captured = interaction.Drain();
        captured.ShouldHaveSingleItem().Method.ShouldBe("sampling/createMessage");
        captured[0].Response.ShouldNotBeNull().ShouldContain("refused");
    }

    [Fact]
    public async Task Notification_IsCapturedWithSummary()
    {
        var interaction = new TuiServerInteraction();

        await interaction.OnNotificationAsync("notifications/message", System.Text.Json.Nodes.JsonNode.Parse("""{"level":"info","data":"hi"}"""), CancellationToken.None);

        var captured = interaction.Drain();
        captured.ShouldHaveSingleItem().Method.ShouldBe("notifications/message");
        captured[0].Detail.ShouldContain("info");
    }

    [Fact]
    public async Task Drain_IsOneShot()
    {
        var interaction = new TuiServerInteraction();
        await interaction.ListRootsAsync(new ListRootsRequestParams(), CancellationToken.None);

        interaction.Drain().Count.ShouldBe(1);
        interaction.Drain().ShouldBeEmpty();
    }
}
