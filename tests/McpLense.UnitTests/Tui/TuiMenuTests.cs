using System;
using McpLense;
using Shouldly;
using Spectre.Console.Testing;
using Xunit;

namespace McpLense.UnitTests.Tui;

public class TuiMenuTests
{
    private static TestConsole NewConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = 200;
        return console;
    }

    private static readonly string[] Items = ["alpha", "bravo", "charlie"];

    [Fact]
    public void Enter_SelectsHighlightedRow_DefaultsToFirst()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Enter);

        var result = TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { BackLabel = "Back" });

        result.Action.ShouldBe(TuiMenuAction.Item);
        result.Index.ShouldBe(0);
    }

    [Fact]
    public void NumberKey_JumpsStraightToThatRow()
    {
        var console = NewConsole();
        console.Input.PushCharacter('3');

        var result = TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { BackLabel = "Back" });

        result.Action.ShouldBe(TuiMenuAction.Item);
        result.Index.ShouldBe(2); // '3' -> third row (zero-based index 2)
    }

    [Fact]
    public void NumberOutOfRange_IsIgnored()
    {
        var console = NewConsole();
        console.Input.PushCharacter('8'); // only three rows -> ignored
        console.Input.PushKey(ConsoleKey.Escape);

        var result = TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { BackLabel = "Back" });

        result.Action.ShouldBe(TuiMenuAction.Back);
    }

    [Fact]
    public void DownArrowThenEnter_SelectsSecondRow()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { BackLabel = "Back" });

        result.Index.ShouldBe(1);
    }

    [Fact]
    public void VimKeys_NavigateDownAndUp()
    {
        var console = NewConsole();
        console.Input.PushCharacter('j'); // down -> 1
        console.Input.PushCharacter('j'); // down -> 2
        console.Input.PushCharacter('k'); // up   -> 1
        console.Input.PushKey(ConsoleKey.Enter);

        var result = TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { BackLabel = "Back" });

        result.Index.ShouldBe(1);
    }

    [Fact]
    public void Escape_ReturnsBack_WhenBackEnabled()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Escape);

        var result = TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { BackLabel = "Back" });

        result.Action.ShouldBe(TuiMenuAction.Back);
    }

    [Fact]
    public void Quit_ReturnsExit_WhenExitEnabled()
    {
        var console = NewConsole();
        console.Input.PushCharacter('q');

        var result = TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { ExitLabel = "Exit" });

        result.Action.ShouldBe(TuiMenuAction.Exit);
    }

    [Fact]
    public void Slash_ReturnsSearch_WhenSearchEnabled()
    {
        var console = NewConsole();
        console.Input.PushCharacter('/');

        var result = TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { ShowSearch = true, BackLabel = "Back" });

        result.Action.ShouldBe(TuiMenuAction.Search);
    }

    [Fact]
    public void C_ReturnsClearFilter_WhenClearEnabled()
    {
        var console = NewConsole();
        console.Input.PushCharacter('c');

        var result = TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { ShowClearFilter = true, BackLabel = "Back" });

        result.Action.ShouldBe(TuiMenuAction.ClearFilter);
    }

    [Fact]
    public void Footer_AlwaysListsTheActiveKeybindings()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Escape);

        TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions
        {
            ShowSearch = true,
            ShowClearFilter = true,
            BackLabel = "Back"
        });

        var output = console.Output;
        output.ShouldContain("move");
        output.ShouldContain("jump");
        output.ShouldContain("select");
        output.ShouldContain("search");
        output.ShouldContain("clear filter");
        output.ShouldContain("back");
    }

    [Fact]
    public void Rows_AreNumberedAndShown()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Escape);

        TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { BackLabel = "Back" });

        var output = console.Output;
        output.ShouldContain("1.");
        output.ShouldContain("alpha");
        output.ShouldContain("3.");
        output.ShouldContain("charlie");
    }

    [Fact]
    public void EmptyList_ShowsPlaceholder_AndBacksOut()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Escape);

        var result = TuiMenu.Select(console, null, "Pick", Array.Empty<string>(), new TuiMenuOptions { BackLabel = "Back" });

        result.Action.ShouldBe(TuiMenuAction.Back);
        console.Output.ShouldContain("nothing to select");
    }

    [Fact]
    public void ExhaustedInput_DoesNotHang_AndLeavesGracefully()
    {
        var console = NewConsole(); // no keys pushed at all

        var result = TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { ExitLabel = "Exit" });

        result.Action.ShouldBe(TuiMenuAction.Exit);
    }
}
