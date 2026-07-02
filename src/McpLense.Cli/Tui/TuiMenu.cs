using Spectre.Console;

namespace McpLense;

/// <summary>What the user did on a <see cref="TuiMenu"/> screen.</summary>
internal enum TuiMenuAction
{
    Item,
    Back,
    Search,
    ClearFilter,
    Exit
}

/// <summary>Outcome of a <see cref="TuiMenu.Select"/> call. <see cref="Index"/> is only meaningful for <see cref="TuiMenuAction.Item"/>.</summary>
internal readonly record struct TuiMenuResult(TuiMenuAction Action, int Index)
{
    public static TuiMenuResult Item(int index) => new(TuiMenuAction.Item, index);
    public static TuiMenuResult Back { get; } = new(TuiMenuAction.Back, -1);
    public static TuiMenuResult Search { get; } = new(TuiMenuAction.Search, -1);
    public static TuiMenuResult ClearFilter { get; } = new(TuiMenuAction.ClearFilter, -1);
    public static TuiMenuResult Exit { get; } = new(TuiMenuAction.Exit, -1);
}

/// <summary>Which control affordances a menu exposes (as keybindings shown in the footer).</summary>
internal sealed class TuiMenuOptions
{
    public bool ShowSearch { get; init; }
    public bool ShowClearFilter { get; init; }

    /// <summary>Label for the back action (Esc / b / ←). Null disables Back.</summary>
    public string? BackLabel { get; init; }

    /// <summary>Label for the exit action (q). Null disables Exit.</summary>
    public string? ExitLabel { get; init; }

    /// <summary>Rows shown per page; the first nine are number-selectable.</summary>
    public int PageSize { get; init; } = 9;

    /// <summary>
    /// Number of content rows in the fixed-height detail pane rendered under the list (when a detail
    /// provider is supplied). Fixed so the layout never "jumps" as you move between items of
    /// different description length; content taller than this is cropped and scrollable in place.
    /// </summary>
    public int DetailHeight { get; init; } = 6;

    /// <summary>
    /// When true, item strings are treated as Spectre MARKUP and rendered as-is instead of being
    /// escaped as literal text. Use only for items the caller fully controls (and has escaped any
    /// dynamic content in) - e.g. a colour-coded picker. Default false: items are literal text, so a
    /// value like <c>"[red]"</c> shows verbatim rather than colouring.
    /// </summary>
    public bool RichItems { get; init; }
}

/// <summary>
/// The content of the fixed-height detail pane for one highlighted item: a header (Spectre markup),
/// an accent colour for the border, and the body as a list of styled plain-text lines.
/// <see cref="TuiMenu"/> word-wraps the plain text to the pane width and scrolls within a fixed box,
/// so long descriptions never grow the layout. Body lines carry PLAIN text (not markup) so wrapping
/// can never split a markup tag - the per-line <see cref="TuiDetailLine.Style"/> is applied after
/// wrapping.
/// </summary>
internal sealed record TuiDetail(string Header, Color Accent, IReadOnlyList<TuiDetailLine> Lines);

/// <summary>One plain-text body line for a <see cref="TuiDetail"/> plus the Spectre style tag to apply.</summary>
internal readonly record struct TuiDetailLine(string Text, string Style = "grey")
{
    public static implicit operator TuiDetailLine(string text) => new(text);
}

/// <summary>
/// A keyboard-driven selection list rendered with Spectre.Console. Unlike Spectre's built-in
/// <c>SelectionPrompt</c> it always shows a persistent keybinding footer and supports
/// number hot-keys (1-9 jump straight to a row) in addition to arrow / vim navigation, which is the
/// "more convenient" interaction the explorer wants. Input is read through
/// <see cref="IAnsiConsole.Input"/> so it is fully drivable from <c>TestConsole</c>.
/// </summary>
internal static class TuiMenu
{
    public static TuiMenuResult Select(
        IAnsiConsole console,
        Action? renderHeader,
        string title,
        IReadOnlyList<string> items,
        TuiMenuOptions options,
        IReadOnlyList<string?>? itemColors = null,
        Action<int>? renderStatusBar = null,
        Func<int, TuiDetail?>? detailFor = null)
    {
        var count = items.Count;
        var index = count > 0 ? 0 : -1;
        var pageSize = Math.Max(1, options.PageSize);
        var top = 0;

        // Scroll offset (in wrapped lines) into the fixed-height detail pane for the current item.
        var detailScroll = 0;
        var lastDetailIndex = index;

        while (true)
        {
            // Keep the highlighted row inside the current viewport.
            if (index >= 0)
            {
                if (index < top) top = index;
                else if (index >= top + pageSize) top = index - pageSize + 1;
            }
            top = Math.Clamp(top, 0, Math.Max(0, count - pageSize));

            // Reset the detail scroll whenever the highlighted item changes, so each item's detail
            // starts at the top.
            if (index != lastDetailIndex)
            {
                detailScroll = 0;
                lastDetailIndex = index;
            }

            console.Clear();
            renderHeader?.Invoke();
            var detailLineCount = Render(console, title, items, index, top, pageSize, options, itemColors, renderStatusBar, detailFor, detailScroll);

            // Maximum scroll = lines beyond the fixed detail window.
            var maxScroll = Math.Max(0, detailLineCount - options.DetailHeight);
            detailScroll = Math.Clamp(detailScroll, 0, maxScroll);

            var key = TryReadKey(console);
            if (key is null)
            {
                // Redirected / exhausted input (e.g. piped EOF): leave gracefully.
                return options.BackLabel is not null ? TuiMenuResult.Back
                    : options.ExitLabel is not null ? TuiMenuResult.Exit
                    : TuiMenuResult.Item(Math.Max(index, 0));
            }

            var info = key.Value;
            var shift = (info.Modifiers & ConsoleModifiers.Shift) != 0;

            // Detail-pane scroll: Shift+Up/Down or '<' / '>' scroll the detail without moving the
            // list selection (Lazygit-style in-pane scroll).
            if ((shift && info.Key is ConsoleKey.DownArrow) || info.KeyChar == '>')
            {
                detailScroll = Math.Min(detailScroll + 1, maxScroll);
                continue;
            }
            if ((shift && info.Key is ConsoleKey.UpArrow) || info.KeyChar == '<')
            {
                detailScroll = Math.Max(detailScroll - 1, 0);
                continue;
            }

            switch (info.Key)
            {
                case ConsoleKey.UpArrow: index = Step(index, -1, count); continue;
                case ConsoleKey.DownArrow: index = Step(index, +1, count); continue;
                case ConsoleKey.Home: if (count > 0) index = 0; continue;
                case ConsoleKey.End: if (count > 0) index = count - 1; continue;
                case ConsoleKey.PageUp: index = Step(index, -pageSize, count); continue;
                case ConsoleKey.PageDown: index = Step(index, +pageSize, count); continue;
                case ConsoleKey.Enter or ConsoleKey.Spacebar or ConsoleKey.RightArrow:
                    if (index >= 0) return TuiMenuResult.Item(index);
                    continue;
                case ConsoleKey.Escape or ConsoleKey.LeftArrow or ConsoleKey.Backspace:
                    if (BackOrExit(options) is { } onEscape) return onEscape;
                    continue;
            }

            var ch = char.ToLowerInvariant(info.KeyChar);
            if (ch == 'j') { index = Step(index, +1, count); continue; }
            if (ch == 'k') { index = Step(index, -1, count); continue; }

            if (ch is >= '1' and <= '9')
            {
                var target = top + (ch - '1');
                if (target < Math.Min(count, top + pageSize)) return TuiMenuResult.Item(target);
                continue;
            }

            if (options.ShowSearch && ch is '/' or 's') return TuiMenuResult.Search;
            if (options.ShowClearFilter && ch == 'c') return TuiMenuResult.ClearFilter;
            if (ch == 'b' && options.BackLabel is not null) return TuiMenuResult.Back;
            if (ch == 'q' && BackOrExit(options) is { } onQuit) return onQuit;
        }
    }

    private static TuiMenuResult? BackOrExit(TuiMenuOptions options)
        => options.ExitLabel is not null ? TuiMenuResult.Exit
            : options.BackLabel is not null ? TuiMenuResult.Back
            : null;

    private static int Step(int index, int delta, int count)
    {
        if (count == 0) return -1;
        if (index < 0) return delta > 0 ? 0 : count - 1;
        return Math.Clamp(index + delta, 0, count - 1);
    }

    /// <summary>Renders one frame; returns the total wrapped-line count of the detail pane (0 when none), so the caller can clamp scrolling.</summary>
    private static int Render(
        IAnsiConsole console,
        string title,
        IReadOnlyList<string> items,
        int index,
        int top,
        int pageSize,
        TuiMenuOptions options,
        IReadOnlyList<string?>? itemColors,
        Action<int>? renderStatusBar,
        Func<int, TuiDetail?>? detailFor,
        int detailScroll)
    {
        if (!string.IsNullOrEmpty(title))
        {
            console.MarkupLine($"[bold]{Markup.Escape(title)}[/]");
        }

        if (items.Count == 0)
        {
            console.MarkupLine("[grey](nothing to select here)[/]");
        }
        else
        {
            var end = Math.Min(items.Count, top + pageSize);
            for (var i = top; i < end; i++)
            {
                var positionInPage = i - top;
                var hotkey = positionInPage < 9 ? (positionInPage + 1).ToString() : " ";
                // Rich items are pre-formatted markup (caller-escaped); plain items are literal text.
                var text = options.RichItems ? items[i] : Markup.Escape(items[i]);
                var color = itemColors is not null && i < itemColors.Count ? itemColors[i] : null;

                if (i == index)
                {
                    if (options.RichItems)
                    {
                        // The item carries its own colours; an inverted background would clash, so cue
                        // the selection with a bold caret + a highlighted hotkey instead.
                        console.MarkupLine($"[bold green]\u203a[/] [bold] {hotkey}.[/]  {text}");
                    }
                    else
                    {
                        // Highlighted row: invert on the row colour (falling back to green) so the
                        // selection cue itself carries the status - an unreachable row stays red even
                        // when it's the active row.
                        var highlight = string.IsNullOrEmpty(color) ? "green" : color;
                        console.MarkupLine($"[bold {highlight}]\u203a[/] [black on {highlight}] {hotkey}.  {text} [/]");
                    }
                }
                else
                {
                    var body = string.IsNullOrEmpty(color) ? text : $"[{color}]{text}[/]";
                    console.MarkupLine($"  [aqua]{hotkey}.[/] {body}");
                }
            }

            if (top > 0 || end < items.Count)
            {
                console.MarkupLine($"[grey]  ({top + 1}-{end} of {items.Count})[/]");
            }
        }

        // An optional caller-supplied status line rendered directly under the items (above the
        // keybinding footer) - e.g. the section-menu counts bar or a log tail.
        // Receives the currently-highlighted index (-1 when the list is empty).
        renderStatusBar?.Invoke(index);

        // The fixed-height, scrollable detail pane for the highlighted item.
        var detailTotal = 0;
        var detail = detailFor is not null && index >= 0 ? detailFor(index) : null;
        if (detail is not null)
        {
            detailTotal = RenderDetail(console, detail, options.DetailHeight, detailScroll);
        }

        RenderFooter(console, options, items.Count, detailTotal > options.DetailHeight);
        return detailTotal;
    }

    /// <summary>
    /// Renders the fixed-height detail pane: word-wraps the detail's lines to the pane width, shows a
    /// <paramref name="height"/>-row window starting at <paramref name="scroll"/>, and appends a
    /// scroll indicator when there's more above/below. Returns the total wrapped-line count.
    /// </summary>
    private static int RenderDetail(IAnsiConsole console, TuiDetail detail, int height, int scroll)
    {
        height = Math.Max(1, height);
        // Inner width = console width minus the panel's borders (2) + horizontal padding (2).
        var innerWidth = Math.Max(10, console.Profile.Width - 4);

        // Wrap each plain-text body line, then apply its style as markup AFTER wrapping so a wrap
        // point can never orphan a markup tag (which would blow up Spectre's parser).
        var wrapped = new List<string>();
        foreach (var line in detail.Lines)
        {
            var pieces = WrapPlainLine(line.Text, innerWidth);
            foreach (var piece in pieces)
            {
                wrapped.Add(string.IsNullOrEmpty(piece)
                    ? string.Empty
                    : $"[{line.Style}]{Markup.Escape(piece)}[/]");
            }
        }
        if (wrapped.Count == 0)
        {
            wrapped.Add("[grey35](nothing to show)[/]");
        }

        var total = wrapped.Count;
        var maxScroll = Math.Max(0, total - height);
        scroll = Math.Clamp(scroll, 0, maxScroll);

        var window = wrapped.Skip(scroll).Take(height).ToList();
        // Pad to a constant height so the box (and everything below it) never shifts.
        while (window.Count < height)
        {
            window.Add(string.Empty);
        }

        var body = string.Join("\n", window);
        var header = detail.Header;
        if (total > height)
        {
            var more = total - height - scroll;
            var scrollHint = scroll > 0 && more > 0 ? $"\u2191{scroll} \u2193{more}"
                : scroll > 0 ? $"\u2191{scroll}"
                : $"\u2193{more}";
            header += $" [grey35]({scrollHint} · shift+\u2191\u2193 scroll)[/]";
        }

        console.Write(new Panel(new Markup(body))
        {
            Header = new PanelHeader($" {header} "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: detail.Accent),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true,
            Height = height + 2 // content rows + top/bottom border
        });

        return total;
    }

    /// <summary>
    /// Word-wraps a plain-text line to <paramref name="width"/> columns, breaking on spaces. A word
    /// longer than the width is hard-broken into width-sized chunks. Returns at least one line.
    /// </summary>
    private static IEnumerable<string> WrapPlainLine(string line, int width)
    {
        if (string.IsNullOrEmpty(line))
        {
            yield return string.Empty;
            yield break;
        }

        var current = new System.Text.StringBuilder();
        foreach (var word in line.Split(' '))
        {
            var piece = word;
            // Hard-break a single word that's wider than the pane.
            while (piece.Length > width)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                yield return piece[..width];
                piece = piece[width..];
            }

            var addition = current.Length == 0 ? piece.Length : piece.Length + 1;
            if (current.Length > 0 && current.Length + addition > width)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }
            current.Append(piece);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static void RenderFooter(IAnsiConsole console, TuiMenuOptions options, int count, bool scrollable)
    {
        var keys = new List<string>();
        if (count > 0)
        {
            keys.Add("[grey]\u2191/\u2193[/] move");
            keys.Add("[grey]1-9[/] jump");
            keys.Add("[grey]enter[/] select");
        }
        if (scrollable) keys.Add("[grey]shift+\u2191/\u2193[/] scroll");
        if (options.ShowSearch) keys.Add("[grey]/[/] search");
        if (options.ShowClearFilter) keys.Add("[grey]c[/] clear filter");
        if (options.BackLabel is not null) keys.Add($"[grey]esc[/] {options.BackLabel.ToLowerInvariant()}");
        if (options.ExitLabel is not null) keys.Add($"[grey]q[/] {options.ExitLabel.ToLowerInvariant()}");

        console.Write(new Rule().RuleStyle(Style.Parse("grey")).LeftJustified());
        console.MarkupLine(keys.Count == 0 ? "[grey](no actions)[/]" : string.Join("   ", keys));
    }

    private static ConsoleKeyInfo? TryReadKey(IAnsiConsole console)
    {
        try
        {
            return console.Input.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
