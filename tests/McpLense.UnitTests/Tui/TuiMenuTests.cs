using System;
using McpLense;
using Shouldly;
using Spectre.Console;
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
    public void PlainItems_MarkupIsEscaped_ShownLiterally()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Escape);

        TuiMenu.Select(console, null, "Pick", new[] { "[red]ERROR[/] boom" },
            new TuiMenuOptions { BackLabel = "Back" });

        // Default (plain) items: the markup is escaped, so the tag appears verbatim as text.
        console.Output.ShouldContain("[red]ERROR[/]");
    }

    [Fact]
    public void RichItems_MarkupIsRendered_NotLiteral()
    {
        // Emit raw ANSI so the applied colour shows up (default TestConsole strips styling).
        var console = new TestConsole().EmitAnsiSequences();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = 80;
        console.Input.PushKey(ConsoleKey.DownArrow); // highlight row 2 so row 1's colour isn't the inverted highlight
        console.Input.PushKey(ConsoleKey.Escape);

        TuiMenu.Select(console, null, "Pick", new[] { "[red]ERROR[/] boom", "plain" },
            new TuiMenuOptions { BackLabel = "Back", RichItems = true });

        var output = console.Output;
        output.ShouldNotContain("[red]ERROR[/]"); // not literal
        output.ShouldContain("38;5;9");            // red 256-colour actually applied
        output.ShouldContain("ERROR");
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
    public void ItemColors_TintTheMatchingRowRed()
    {
        // Emit raw ANSI so the colour actually shows up in the captured output (the default
        // TestConsole strips styling). A red item must carry the red SGR code (foreground 31).
        var console = new TestConsole().EmitAnsiSequences();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = 200;
        console.Input.PushKey(ConsoleKey.Escape);

        // Row 0 highlighted+red, row 1 uncoloured.
        TuiMenu.Select(
            console,
            null,
            "Pick",
            ["unreachable-one", "healthy-two"],
            new TuiMenuOptions { BackLabel = "Back" },
            itemColors: ["red", null]);

        // 38;5;9 = red (256-colour) foreground; 38;5;10 = green (the default highlight). The
        // coloured row must be red, not the default green highlight.
        var output = console.Output;
        output.ShouldContain("38;5;9");
        output.ShouldContain("unreachable-one");
    }

    [Fact]
    public void ExhaustedInput_DoesNotHang_AndLeavesGracefully()
    {
        var console = NewConsole(); // no keys pushed at all

        var result = TuiMenu.Select(console, null, "Pick", Items, new TuiMenuOptions { ExitLabel = "Exit" });

        result.Action.ShouldBe(TuiMenuAction.Exit);
    }

    [Fact]
    public void RenderStatusBar_ReceivesHighlightedIndex_AndUpdatesOnMove()
    {
        var console = NewConsole();
        var seen = new System.Collections.Generic.List<int>();
        console.Input.PushKey(ConsoleKey.DownArrow); // move 0 -> 1
        console.Input.PushKey(ConsoleKey.Escape);

        TuiMenu.Select(
            console, null, "Pick", Items,
            new TuiMenuOptions { BackLabel = "Back" },
            renderStatusBar: i => seen.Add(i));

        // Rendered once at index 0, then again at index 1 after the DownArrow.
        seen.ShouldContain(0);
        seen.ShouldContain(1);
    }

    [Fact]
    public void RenderStatusBar_EmptyList_ReceivesNegativeIndex()
    {
        var console = NewConsole();
        var seen = new System.Collections.Generic.List<int>();
        console.Input.PushKey(ConsoleKey.Escape);

        TuiMenu.Select(
            console, null, "Pick", System.Array.Empty<string>(),
            new TuiMenuOptions { BackLabel = "Back" },
            renderStatusBar: i => seen.Add(i));

        seen.ShouldAllBe(i => i == -1);
    }

    // --- Fixed-height, scrollable detail pane -----------------------------

    private static TuiDetail LongDetail(int lines)
    {
        var body = new System.Collections.Generic.List<TuiDetailLine>();
        for (var i = 0; i < lines; i++)
        {
            body.Add(new TuiDetailLine($"line-{i:D2}"));
        }
        return new TuiDetail("[bold]detail[/]", Color.Aqua, body);
    }

    [Fact]
    public void Detail_ShowsScrollHint_WhenContentExceedsBox()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Escape);

        TuiMenu.Select(
            console, null, "Pick", Items,
            new TuiMenuOptions { BackLabel = "Back", DetailHeight = 3 },
            detailFor: _ => LongDetail(20));

        // First 3 lines visible; a scroll hint offers "shift" scrolling for the remaining lines.
        var output = console.Output;
        output.ShouldContain("line-00");
        output.ShouldContain("scroll");
        output.ShouldContain("shift"); // footer hint
    }

    [Fact]
    public void Detail_ShiftDown_ScrollsToLaterLines()
    {
        var console = NewConsole();
        console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: true, alt: false, control: false));
        console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: true, alt: false, control: false));
        console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: true, alt: false, control: false));
        console.Input.PushKey(ConsoleKey.Escape);

        TuiMenu.Select(
            console, null, "Pick", Items,
            new TuiMenuOptions { BackLabel = "Back", DetailHeight = 3 },
            detailFor: _ => LongDetail(20));

        // After scrolling down 3, a line from further down the content is now visible.
        console.Output.ShouldContain("line-03");
    }

    [Fact]
    public void Detail_AngleBrackets_AlsoScroll()
    {
        var console = NewConsole();
        console.Input.PushCharacter('>');
        console.Input.PushCharacter('>');
        console.Input.PushKey(ConsoleKey.Escape);

        TuiMenu.Select(
            console, null, "Pick", Items,
            new TuiMenuOptions { BackLabel = "Back", DetailHeight = 2 },
            detailFor: _ => LongDetail(20));

        console.Output.ShouldContain("line-02");
    }

    [Fact]
    public void Detail_ScrollDoesNotChangeSelection()
    {
        var console = NewConsole();
        console.Input.PushCharacter('>'); // scroll, not move
        console.Input.PushKey(ConsoleKey.Enter); // select highlighted

        var result = TuiMenu.Select(
            console, null, "Pick", Items,
            new TuiMenuOptions { BackLabel = "Back", DetailHeight = 2 },
            detailFor: _ => LongDetail(20));

        result.Action.ShouldBe(TuiMenuAction.Item);
        result.Index.ShouldBe(0); // still the first item - scrolling didn't move the cursor
    }

    [Fact]
    public void Detail_ShortContent_NoScrollHint()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Escape);

        TuiMenu.Select(
            console, null, "Pick", Items,
            new TuiMenuOptions { BackLabel = "Back", DetailHeight = 6 },
            detailFor: _ => LongDetail(2));

        // 2 lines fit in a 6-row box - no scroll affordance.
        console.Output.ShouldNotContain("scroll");
    }
}
