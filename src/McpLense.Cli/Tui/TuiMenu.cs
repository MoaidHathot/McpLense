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
        Action<int>? renderStatusBar = null)
    {
        var count = items.Count;
        var index = count > 0 ? 0 : -1;
        var pageSize = Math.Max(1, options.PageSize);
        var top = 0;

        while (true)
        {
            // Keep the highlighted row inside the current viewport.
            if (index >= 0)
            {
                if (index < top) top = index;
                else if (index >= top + pageSize) top = index - pageSize + 1;
            }
            top = Math.Clamp(top, 0, Math.Max(0, count - pageSize));

            console.Clear();
            renderHeader?.Invoke();
            Render(console, title, items, index, top, pageSize, options, itemColors, renderStatusBar);

            var key = TryReadKey(console);
            if (key is null)
            {
                // Redirected / exhausted input (e.g. piped EOF): leave gracefully.
                return options.BackLabel is not null ? TuiMenuResult.Back
                    : options.ExitLabel is not null ? TuiMenuResult.Exit
                    : TuiMenuResult.Item(Math.Max(index, 0));
            }

            var info = key.Value;
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

    private static void Render(
        IAnsiConsole console,
        string title,
        IReadOnlyList<string> items,
        int index,
        int top,
        int pageSize,
        TuiMenuOptions options,
        IReadOnlyList<string?>? itemColors,
        Action<int>? renderStatusBar)
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
                var text = Markup.Escape(items[i]);
                var color = itemColors is not null && i < itemColors.Count ? itemColors[i] : null;

                if (i == index)
                {
                    // Highlighted row: invert on the row colour (falling back to green) so the
                    // selection cue itself carries the status - an unreachable row stays red even
                    // when it's the active row.
                    var highlight = string.IsNullOrEmpty(color) ? "green" : color;
                    console.MarkupLine($"[bold {highlight}]\u203a[/] [black on {highlight}] {hotkey}.  {text} [/]");
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
        // keybinding footer) - e.g. the section-menu counts bar or a per-selection detail panel.
        // Receives the currently-highlighted index (-1 when the list is empty).
        renderStatusBar?.Invoke(index);

        RenderFooter(console, options, items.Count);
    }

    private static void RenderFooter(IAnsiConsole console, TuiMenuOptions options, int count)
    {
        var keys = new List<string>();
        if (count > 0)
        {
            keys.Add("[grey]\u2191/\u2193[/] move");
            keys.Add("[grey]1-9[/] jump");
            keys.Add("[grey]enter[/] select");
        }
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
