using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using Spectre.Console;
using Spectre.Console.Json;

namespace McpLense;

internal static class TuiApp
{
    public static Task<int> RunAsync(ParsedCommand command)
        => RunAsync(command, console: null, waitForKey: null, bookmarkStore: null);

    internal static async Task<int> RunAsync(
        ParsedCommand command,
        IAnsiConsole? console,
        Func<Task>? waitForKey,
        TuiBookmarkStore? bookmarkStore = null)
    {
        console ??= AnsiConsole.Console;
        waitForKey ??= DefaultWaitForKey;
        bookmarkStore ??= TuiBookmarkStore.LoadDefault();

        var inspectCommand = command with
        {
            Command = AppCommand.Inspect,
            ProgressEnabled = false
        };

        var outcome = await McpExecutor.ExecuteAsync(inspectCommand, App.JsonOptions, CancellationToken.None);
        if (outcome.Payload is not InspectReport report)
        {
            throw new InvalidOperationException("TUI expected an inspect report.");
        }

        // Invocations open their own live session per server-visit (reusing the same parsed
        // command), so a tool call / read / prompt authenticates exactly the way the equivalent
        // CLI command would - and for stdio the server process stays up across calls. A TUI
        // interaction captures the server-initiated half of the protocol so it can be shown after
        // each invocation (and, with --server-stream, on the next invocation when idle).
        var interaction = new TuiServerInteraction();
        var connector = InvocationRenderer.ConnectorFor(command, interaction);
        return await RenderAsync(report, console, waitForKey, bookmarkStore, connector, interaction);
    }

    internal static Task<int> RenderAsync(
        InspectReport report,
        IAnsiConsole console,
        Func<Task> waitForKey)
        => RenderAsync(report, console, waitForKey, bookmarkStore: null, connector: null, interaction: null);

    internal static Task<int> RenderAsync(
        InspectReport report,
        IAnsiConsole console,
        Func<Task> waitForKey,
        TuiBookmarkStore? bookmarkStore)
        => RenderAsync(report, console, waitForKey, bookmarkStore, connector: null, interaction: null);

    internal static Task<int> RenderAsync(
        InspectReport report,
        IAnsiConsole console,
        Func<Task> waitForKey,
        TuiBookmarkStore? bookmarkStore,
        McpSessionConnector? connector)
        => RenderAsync(report, console, waitForKey, bookmarkStore, connector, interaction: null);

    internal static async Task<int> RenderAsync(
        InspectReport report,
        IAnsiConsole console,
        Func<Task> waitForKey,
        TuiBookmarkStore? bookmarkStore,
        McpSessionConnector? connector,
        TuiServerInteraction? interaction)
    {
        var servers = report.Servers;
        if (servers.Count == 0)
        {
            console.MarkupLine("[red]No servers were resolved.[/]");
            return 1;
        }

        bookmarkStore ??= TuiBookmarkStore.InMemory();
        var session = new TuiSession(console, waitForKey, bookmarkStore, connector, interaction);

        // A single resolved server has nothing to pick between: skip the selection pre-form and jump
        // straight into it. Backing out of the section menu then exits the TUI (there is no list to
        // return to), which RunServerLoopAsync handles via the singleServer flag.
        if (servers.Count == 1)
        {
            await ShowServerAsync(session, servers[0], singleServer: true);
            return 0;
        }

        while (true)
        {
            var serverItems = servers.Select(FormatServerListItem).ToArray();
            var serverColors = servers.Select(s => s.Error is not null ? "red" : (string?)null).ToArray();

            var result = TuiMenu.Select(
                console,
                renderHeader: () => RenderAppHeader(console, $"{servers.Count} MCP servers resolved"),
                title: "Select an MCP server",
                items: serverItems,
                options: new TuiMenuOptions { ExitLabel = "Exit" },
                itemColors: serverColors);

            if (result.Action is not TuiMenuAction.Item)
            {
                return 0;
            }

            await ShowServerAsync(session, servers[result.Index]);
        }
    }

    /// <summary>A compact branded header shown atop top-level screens.</summary>
    internal static void RenderAppHeader(IAnsiConsole console, string subtitle)
    {
        console.Write(new Panel($"[bold aqua]McpLense[/] [grey]\u2502[/] [grey]MCP explorer[/]\n[grey]{Markup.Escape(subtitle)}[/]")
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: Color.Aqua),
            Padding = new Padding(1, 0, 1, 0)
        });
    }

    /// <summary>
    /// One row in the server-selection list. A reachable server shows
    /// <c>\u25cf name   \u2502 transport \u2502   target</c>; an unreachable one appends the concise failure
    /// reason (e.g. <c>(unreachable: 401 Unauthorized)</c>) so the status code is visible without
    /// drilling in. The row is tinted red by the caller via <c>itemColors</c>.
    /// </summary>
    internal static string FormatServerListItem(ServerInspection server)
    {
        // The leading dot inherits the row colour (green when reachable, red when not) applied by
        // the menu's itemColors, giving an at-a-glance health indicator per row.
        var label = $"\u25cf {server.Name}   [{server.Transport}]   {server.Target}";
        if (server.Error is null)
        {
            return label;
        }

        var reason = DescribeConnectionFailure(server.Error);
        return reason is null
            ? $"{label}   (unreachable)"
            : $"{label}   (unreachable: {reason})";
    }

    private static async Task ShowServerAsync(TuiSession session, ServerInspection server, bool singleServer = false)
    {
        // Section-level filter state. Each section keeps its own filter so jumping between
        // Tools / Resources doesn't carry the term over; that matches user intuition for
        // a section-scoped search.
        var filters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Tools"] = string.Empty,
            ["Resources"] = string.Empty,
            ["Resource Templates"] = string.Empty,
            ["Prompts"] = string.Empty
        };

        try
        {
            await RunServerLoopAsync(session, server, filters, singleServer);
        }
        finally
        {
            // A live invocation session is opened lazily on first invoke; close it when leaving
            // the server so we don't hold the transport (or keep a stdio process up) afterwards.
            await session.CloseSessionAsync();
        }
    }

    private static async Task RunServerLoopAsync(TuiSession session, ServerInspection server, IDictionary<string, string> filters, bool singleServer = false)
    {
        var console = session.Console;

        // When the server advertises logging, open the session up-front and ask for the most verbose
        // level so log messages stream from the moment we enter the server (not just after the first
        // tool call). Best-effort: a failure just leaves logging off.
        await TryEnableLoggingAsync(session, server);

        while (true)
        {
            var bookmarksForServer = session.Bookmarks.ForServer(server.Name);
            var bookmarksLabel = bookmarksForServer.Count == 0
                ? "Bookmarks"
                : $"Bookmarks ({bookmarksForServer.Count})";

            var logsLabel = BuildLogsLabel(session, server);

            var sections = server.Capabilities.Logging
                ? new[] { "Overview", "Tools", "Resources", "Resource Templates", "Prompts", bookmarksLabel, logsLabel }
                : new[] { "Overview", "Tools", "Resources", "Resource Templates", "Prompts", bookmarksLabel };

            // With a single auto-selected server there is no server list to go back to, so the
            // back affordance becomes a plain Exit instead of "Back to servers".
            var menuOptions = singleServer
                ? new TuiMenuOptions { ExitLabel = "Exit" }
                : new TuiMenuOptions { BackLabel = "Back to servers" };

            var result = TuiMenu.Select(
                console,
                renderHeader: () =>
                {
                    RenderServerSummary(console, server);
                    RenderSectionCountsBar(console, server);
                },
                title: "Choose a section",
                items: sections,
                options: menuOptions,
                renderStatusBar: _ => RenderLogTail(session, server));

            if (result.Action is not TuiMenuAction.Item)
            {
                return;
            }

            var choice = sections[result.Index];
            if (choice == bookmarksLabel)
            {
                await ShowBookmarksAsync(console, server, session.Bookmarks, session.WaitForKey);
                continue;
            }

            if (choice == logsLabel)
            {
                await ShowLogsAsync(session, server);
                continue;
            }

            switch (choice)
            {
                case "Overview":
                    console.Clear();
                    RenderServerSummary(console, server);
                    RenderOverview(console, server);
                    console.MarkupLine("\n[grey]Press any key to continue...[/]");
                    await session.WaitForKey();
                    break;
                case "Tools":
                    await ShowToolsAsync(session, server, filters);
                    break;
                case "Resources":
                    await ShowResourcesAsync(session, server, filters);
                    break;
                case "Resource Templates":
                    await ShowResourceTemplatesAsync(session, server, filters);
                    break;
                case "Prompts":
                    await ShowPromptsAsync(session, server, filters);
                    break;
            }
        }
    }

    // --- Sections with search + bookmarks + drilldown ---------------

    private static Task ShowToolsAsync(TuiSession session, ServerInspection server, IDictionary<string, string> filters)
        => RunListAsync(
            session, server, filters, "Tools", server.Tools.Error, server.Tools.Items.Count,
            filter => FilterTools(server.Tools.Items, filter),
            ToolDisplay,
            tool => ShowToolDetailAsync(session, server, tool),
            BuildToolListDetail);

    private static Task ShowToolDetailAsync(TuiSession session, ServerInspection server, ToolInfo tool)
        => RunDetailAsync(
            session, server,
            () => RenderToolDetail(session.Console, tool),
            new TuiBookmark(server.Name, TuiBookmarkKind.Tool, tool.Name),
            session.Connector is not null ? CallChoice : null,
            () => InvokeToolAsync(session, server, tool));

    private static Task ShowResourcesAsync(TuiSession session, ServerInspection server, IDictionary<string, string> filters)
        => RunListAsync(
            session, server, filters, "Resources", server.Resources.Error, server.Resources.Items.Count,
            filter => FilterResources(server.Resources.Items, filter),
            ResourceDisplay,
            resource => ShowResourceDetailAsync(session, server, resource),
            BuildResourceListDetail);

    private static Task ShowResourceDetailAsync(TuiSession session, ServerInspection server, ResourceInfo resource)
        => RunDetailAsync(
            session, server,
            () => RenderResourceDetail(session.Console, resource),
            new TuiBookmark(server.Name, TuiBookmarkKind.Resource, resource.Uri ?? resource.Name ?? "(unnamed)"),
            session.Connector is not null && !string.IsNullOrEmpty(resource.Uri) ? ReadChoice : null,
            () => InvokeResourceAsync(session, server, resource.Uri!));

    private static Task ShowResourceTemplatesAsync(TuiSession session, ServerInspection server, IDictionary<string, string> filters)
        => RunListAsync(
            session, server, filters, "Resource Templates", server.ResourceTemplates.Error, server.ResourceTemplates.Items.Count,
            filter => FilterResourceTemplates(server.ResourceTemplates.Items, filter),
            ResourceTemplateDisplay,
            template => ShowResourceTemplateDetailAsync(session, server, template),
            BuildResourceTemplateListDetail);

    private static Task ShowResourceTemplateDetailAsync(TuiSession session, ServerInspection server, ResourceTemplateInfo template)
        => RunDetailAsync(
            session, server,
            () => RenderResourceTemplateDetail(session.Console, template),
            new TuiBookmark(server.Name, TuiBookmarkKind.ResourceTemplate, template.UriTemplate ?? template.Name ?? "(unnamed)"),
            session.Connector is not null && !string.IsNullOrEmpty(template.UriTemplate) ? ReadChoice : null,
            () => InvokeTemplateAsync(session, server, template));

    private static Task ShowPromptsAsync(TuiSession session, ServerInspection server, IDictionary<string, string> filters)
        => RunListAsync(
            session, server, filters, "Prompts", server.Prompts.Error, server.Prompts.Items.Count,
            filter => FilterPrompts(server.Prompts.Items, filter),
            PromptDisplay,
            prompt => ShowPromptDetailAsync(session, server, prompt),
            BuildPromptListDetail);

    private static Task ShowPromptDetailAsync(TuiSession session, ServerInspection server, PromptInfo prompt)
        => RunDetailAsync(
            session, server,
            () => RenderPromptDetail(session.Console, prompt),
            new TuiBookmark(server.Name, TuiBookmarkKind.Prompt, prompt.Name),
            session.Connector is not null ? GetPromptChoice : null,
            () => InvokePromptAsync(session, server, prompt));

    /// <summary>
    /// Drives one searchable section list: renders the (filtered) items as a numbered
    /// <see cref="TuiMenu"/>, handles search / clear-filter / back, and drills into the chosen item.
    /// Section- or connection-level errors short-circuit to a "press a key" notice.
    /// </summary>
    private static async Task RunListAsync<T>(
        TuiSession session,
        ServerInspection server,
        IDictionary<string, string> filters,
        string sectionKey,
        string? sectionError,
        int totalCount,
        Func<string, IReadOnlyList<T>> matchesFor,
        Func<T, string> display,
        Func<T, Task> drill,
        Func<T, TuiDetail>? buildDetail = null)
    {
        var console = session.Console;
        while (true)
        {
            if (sectionError is not null || server.Error is not null)
            {
                console.Clear();
                RenderServerSummary(console, server);
                if (sectionError is not null)
                {
                    console.MarkupLine($"[red]{Markup.Escape(sectionError)}[/]");
                }
                else
                {
                    TryRenderConnectionError(console, server);
                }

                console.MarkupLine("\n[grey]Press any key to continue...[/]");
                await session.WaitForKey();
                return;
            }

            var filter = filters[sectionKey];
            var matches = matchesFor(filter);
            var items = matches.Select(display).ToArray();

            var result = TuiMenu.Select(
                console,
                renderHeader: () =>
                {
                    RenderServerSummary(console, server);
                    RenderFilterHeader(console, filter, matches.Count, totalCount);
                },
                title: sectionKey,
                items: items,
                options: new TuiMenuOptions
                {
                    ShowSearch = true,
                    ShowClearFilter = filter.Length > 0,
                    BackLabel = "Back"
                },
                detailFor: buildDetail is null
                    ? null
                    // Fixed-height, scrollable detail of the highlighted row (its FULL, untruncated
                    // description) so long descriptions cropped in the one-line list are readable in
                    // full without the layout jumping between items.
                    : index => index >= 0 && index < matches.Count ? buildDetail(matches[index]) : null);

            switch (result.Action)
            {
                case TuiMenuAction.Search:
                    filters[sectionKey] = PromptFilter(console);
                    break;
                case TuiMenuAction.ClearFilter:
                    filters[sectionKey] = string.Empty;
                    break;
                case TuiMenuAction.Item:
                    await drill(matches[result.Index]);
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Drives a drilled-in item's action screen (Call/Read/Get + Bookmark) as a numbered
    /// <see cref="TuiMenu"/>, re-rendering the item detail above it each frame.
    /// </summary>
    private static async Task RunDetailAsync(
        TuiSession session,
        ServerInspection server,
        Action renderDetail,
        TuiBookmark bookmark,
        string? primaryAction,
        Func<Task> invokePrimary)
    {
        var console = session.Console;
        while (true)
        {
            var toggle = session.Bookmarks.Contains(bookmark) ? "Unbookmark" : "Bookmark";
            var actions = new List<string>();
            if (primaryAction is not null)
            {
                actions.Add(primaryAction);
            }
            actions.Add(toggle);

            var result = TuiMenu.Select(
                console,
                renderHeader: () =>
                {
                    RenderServerSummary(console, server);
                    renderDetail();
                },
                title: "Actions",
                items: actions,
                options: new TuiMenuOptions { BackLabel = "Back" });

            if (result.Action is not TuiMenuAction.Item)
            {
                return;
            }

            if (primaryAction is not null && result.Index == 0)
            {
                await invokePrimary();
                continue;
            }

            await ToggleBookmarkAsync(session, bookmark);
        }
    }

    // --- Invocation drivers -----------------------------------------

    private static async Task InvokeToolAsync(TuiSession session, ServerInspection server, ToolInfo tool)
    {
        var mcp = await EnsureSessionAsync(session, server);
        if (mcp is null) return;

        var arguments = ArgumentElicitor.ElicitToolArguments(session.Console, tool.InputSchema);
        if (!ConfirmRun(session, "call", tool.Name, arguments, server)) return;

        var report = await RunWithProgressAsync(session, $"Calling {tool.Name}",
            (progress, ct) => mcp.CallToolAsync(tool.Name, arguments, progress, ct));
        await RenderInvocationResultAsync(session, $"call {tool.Name}", report);
    }

    private static async Task InvokeResourceAsync(TuiSession session, ServerInspection server, string uri)
    {
        var mcp = await EnsureSessionAsync(session, server);
        if (mcp is null) return;

        if (!ConfirmRun(session, "read", uri, arguments: null, server)) return;

        var report = await mcp.ReadResourceAsync(uri, arguments: null, CancellationToken.None);
        await RenderInvocationResultAsync(session, $"read {uri}", report);
    }

    private static async Task InvokeTemplateAsync(TuiSession session, ServerInspection server, ResourceTemplateInfo template)
    {
        var mcp = await EnsureSessionAsync(session, server);
        if (mcp is null) return;

        var uriTemplate = template.UriTemplate!;
        var variables = await ArgumentElicitor.ElicitTemplateVariablesAsync(
            session.Console, uriTemplate, new TemplateCompletionSource(mcp, uriTemplate));

        if (!ConfirmRun(session, "read", uriTemplate, variables, server)) return;

        var report = await mcp.ReadResourceAsync(uriTemplate, variables.Count > 0 ? variables : null, CancellationToken.None);
        await RenderInvocationResultAsync(session, $"read {uriTemplate}", report);
    }

    private static async Task InvokePromptAsync(TuiSession session, ServerInspection server, PromptInfo prompt)
    {
        var mcp = await EnsureSessionAsync(session, server);
        if (mcp is null) return;

        var arguments = await ArgumentElicitor.ElicitPromptArgumentsAsync(
            session.Console, prompt.Arguments, new PromptCompletionSource(mcp, prompt.Name));

        if (!ConfirmRun(session, "prompt", prompt.Name, arguments, server)) return;

        var report = await mcp.GetPromptAsync(prompt.Name, arguments, CancellationToken.None);
        await RenderInvocationResultAsync(session, $"prompt {prompt.Name}", report);
    }

    /// <summary>
    /// Lazily opens the live session for the selected server on first invoke and caches it on the
    /// <see cref="TuiSession"/>; surfaces a friendly message and returns null when the connect fails.
    /// </summary>
    private static async Task<IMcpSession?> EnsureSessionAsync(TuiSession session, ServerInspection server)
    {
        if (session.Mcp is not null) return session.Mcp;
        if (session.Connector is null) return null;

        try
        {
            session.Mcp = await session.Connector(server.Name, CancellationToken.None);
            return session.Mcp;
        }
        catch (Exception ex)
        {
            session.Console.MarkupLine($"[red]Could not open a session: {Markup.Escape(ex.Message)}[/]");
            session.Console.MarkupLine("[grey]Press any key to continue...[/]");
            await session.WaitForKey();
            return null;
        }
    }

    private static bool ConfirmRun(TuiSession session, string verb, string subject, JsonObject? arguments, ServerInspection server)
    {
        var equivalent = ArgumentElicitor.BuildEquivalentCommand(verb, subject, arguments, server.Transport, server.Target);
        session.Console.MarkupLine($"[grey]equivalent:[/] {Markup.Escape(equivalent)}");
        return session.Console.Confirm("Run now?");
    }

    // --- Logging (logging/setLevel + notifications/message stream) -----

    /// <summary>
    /// If the server advertises the <c>logging</c> capability, open the live session up front and
    /// request the most verbose level so every log message streams in. Best-effort and silent: a
    /// connect or setLevel failure just leaves <see cref="TuiSession.LogLevel"/> null (no logs), and
    /// it's only attempted once per server visit. Requires a connector (skipped for library hosts).
    /// </summary>
    private static async Task TryEnableLoggingAsync(TuiSession session, ServerInspection server)
    {
        if (!server.Capabilities.Logging
            || session.Connector is null
            || session.Interaction is null
            || session.LogLevel is not null
            || server.Error is not null)
        {
            return;
        }

        IMcpSession? mcp;
        try
        {
            mcp = session.Mcp ??= await session.Connector(server.Name, CancellationToken.None);
        }
        catch
        {
            return; // Couldn't open a session; logging stays off. A later invoke will report the error.
        }

        await ApplyLogLevelAsync(session, mcp, TuiLogFormat.MostVerbose);
    }

    /// <summary>Sends logging/setLevel and records the level on the session; swallows failures.</summary>
    private static async Task<bool> ApplyLogLevelAsync(TuiSession session, IMcpSession mcp, ModelContextProtocol.Protocol.LoggingLevel level)
    {
        try
        {
            await mcp.SetLoggingLevelAsync(level, CancellationToken.None);
            session.LogLevel = level;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildLogsLabel(TuiSession session, ServerInspection server)
    {
        var count = session.Interaction?.LogCount ?? 0;
        return count == 0 ? "Logs" : $"Logs ({count})";
    }

    /// <summary>
    /// The persistent, always-visible log tail shown under the section menu: the most recent few log
    /// lines, colour-coded by severity, so activity is visible without opening the Logs view. It
    /// re-renders every menu frame, so it survives navigation between sections.
    /// </summary>
    private static void RenderLogTail(TuiSession session, ServerInspection server)
    {
        if (!server.Capabilities.Logging || session.Interaction is null)
        {
            return;
        }

        var console = session.Console;
        var all = session.Interaction.LogSnapshot();
        console.Write(new Rule($"[grey]server logs[/] [grey35]{LogLevelSuffix(session)}[/]").RuleStyle(Style.Parse("grey35")).LeftJustified());

        if (all.Count == 0)
        {
            console.MarkupLine(session.LogLevel is null
                ? "  [grey35](logging not enabled)[/]"
                : "  [grey35](no log messages yet)[/]");
            return;
        }

        const int tail = 5;
        var slice = all.Count <= tail ? all : all.Skip(all.Count - tail).ToList();
        if (all.Count > tail)
        {
            console.MarkupLine($"  [grey35]\u2026 {all.Count - tail} earlier - open Logs for the full history[/]");
        }
        foreach (var entry in slice)
        {
            console.MarkupLine("  " + FormatLogLine(entry, includeTimestamp: false));
        }
    }

    private static string LogLevelSuffix(TuiSession session)
        => session.LogLevel is { } level ? $"(level: {TuiLogFormat.Name(level).ToLowerInvariant()})" : "(off)";

    /// <summary>One log line as markup: severity tag + optional logger + message, tinted by level.</summary>
    internal static string FormatLogLine(TuiLogEntry entry, bool includeTimestamp)
    {
        var colour = TuiLogFormat.Colour(entry.Level);
        var tag = $"[{colour}]{TuiLogFormat.Tag(entry.Level)}[/]";
        var time = includeTimestamp ? $"[grey35]{entry.Timestamp:HH:mm:ss}[/] " : string.Empty;
        var loggerText = TuiLogEntry.Sanitize(entry.Logger);
        var logger = string.IsNullOrEmpty(loggerText) ? string.Empty : $"[grey54]{Markup.Escape(loggerText)}[/] ";
        // Sanitize server-supplied text (strip ANSI/control chars so it can't override our colours or
        // leak a reset), collapse newlines to keep one entry on one line, then escape markup.
        var message = Markup.Escape(TuiLogEntry.Sanitize(entry.Message).ReplaceLineEndings(" ").Trim());
        return $"{time}{tag} {logger}[{colour}]{message}[/]";
    }

    /// <summary>
    /// Full log viewer: every message received since connecting, newest-relevant at the bottom, with
    /// a level picker (logging/setLevel) and refresh. Because notifications arrive on background SDK
    /// threads, re-entering / refreshing shows anything that streamed in while you were away.
    /// </summary>
    private static async Task ShowLogsAsync(TuiSession session, ServerInspection server)
    {
        var console = session.Console;

        while (true)
        {
            var actions = new[] { "Change level", "Refresh", "Back" };
            var result = TuiMenu.Select(
                console,
                renderHeader: () =>
                {
                    RenderServerSummary(console, server);
                    RenderLogViewer(session, server);
                },
                title: "Logs",
                items: actions,
                options: new TuiMenuOptions { BackLabel = "Back" });

            if (result.Action is not TuiMenuAction.Item)
            {
                return;
            }

            switch (actions[result.Index])
            {
                case "Change level":
                    await ChangeLogLevelAsync(session, server);
                    break;
                case "Refresh":
                    break; // loop re-reads the snapshot on the next renderHeader
                default:
                    return;
            }
        }
    }

    /// <summary>Renders the full log history + the current-level line for the Logs viewer header.</summary>
    private static void RenderLogViewer(TuiSession session, ServerInspection server)
    {
        var console = session.Console;
        var entries = session.Interaction?.LogSnapshot() ?? [];
        console.Write(new Rule($"[bold]server logs[/] [grey]{LogLevelSuffix(session)}[/]").RuleStyle(Style.Parse("grey")).LeftJustified());

        if (entries.Count == 0)
        {
            console.MarkupLine(session.LogLevel is null
                ? "[grey]Logging isn't enabled. Pick a level below to start receiving log messages.[/]"
                : "[grey]No log messages have been received yet.[/]");
        }
        else
        {
            // Show the last screenful so the newest are visible; note if older entries scrolled off.
            const int maxRows = 200;
            var shown = entries.Count <= maxRows ? entries : entries.Skip(entries.Count - maxRows).ToList();
            if (entries.Count > maxRows)
            {
                console.MarkupLine($"[grey35]\u2026 {entries.Count - maxRows} earlier entries not shown[/]");
            }
            foreach (var entry in shown)
            {
                console.MarkupLine(FormatLogLine(entry, includeTimestamp: true));
            }
        }

        if (server.Transport == "http" && session.LogLevel is not null)
        {
            console.MarkupLine("[grey35]tip: idle HTTP servers only push logs while a request is open - run --server-stream to keep the stream open.[/]");
        }
    }

    /// <summary>Level picker (the 8 MCP severities); sends logging/setLevel for the chosen level.</summary>
    private static async Task ChangeLogLevelAsync(TuiSession session, ServerInspection server)
    {
        var console = session.Console;
        var mcp = await EnsureSessionAsync(session, server);
        if (mcp is null)
        {
            return;
        }

        var levels = TuiLogFormat.LevelsVerboseFirst;
        var items = levels
            .Select(l =>
            {
                var current = session.LogLevel == l ? "  [green](current)[/]" : string.Empty;
                var verbosity = l == TuiLogFormat.MostVerbose ? "  [grey35](most verbose)[/]" : string.Empty;
                return $"[{TuiLogFormat.Colour(l)}]{TuiLogFormat.Tag(l).Trim()}[/] {Markup.Escape(TuiLogFormat.Name(l))}{verbosity}{current}";
            })
            .ToArray();

        var result = TuiMenu.Select(
            console,
            renderHeader: () => console.MarkupLine("[grey]The server will send log messages at or above the chosen severity.[/]"),
            title: "Set log level",
            items: items,
            options: new TuiMenuOptions { BackLabel = "Back", RichItems = true });

        if (result.Action is not TuiMenuAction.Item)
        {
            return;
        }

        var chosen = levels[result.Index];
        var ok = await ApplyLogLevelAsync(session, mcp, chosen);
        console.MarkupLine(ok
            ? $"[green]Log level set to {TuiLogFormat.Name(chosen).ToLowerInvariant()}.[/]"
            : "[red]The server rejected the log-level request.[/]");
        console.MarkupLine("[grey]Press any key to continue...[/]");
        await session.WaitForKey();
    }

    /// <summary>
    /// Runs a cancellable operation inside a live Spectre progress bar that the server's progress
    /// notifications drive; pressing Esc cancels. The session maps cancellation to a clean error.
    /// </summary>
    private static async Task<T> RunWithProgressAsync<T>(TuiSession session, string title, Func<IProgress<ProgressNotificationValue>, CancellationToken, Task<T>> operation)
    {
        using var cts = new CancellationTokenSource();
        var captured = default(T)!;

        session.Console.MarkupLine("[grey](press Esc to cancel)[/]");
        await session.Console.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(Markup.Escape(title));
                task.IsIndeterminate = true;
                var progress = new Progress<ProgressNotificationValue>(value => ApplyProgress(task, title, value));

                var operationTask = operation(progress, cts.Token);
                while (!operationTask.IsCompleted)
                {
                    if (EscapePressed())
                    {
                        cts.Cancel();
                    }

                    await Task.WhenAny(operationTask, Task.Delay(80)).ConfigureAwait(false);
                }

                captured = await operationTask.ConfigureAwait(false);
                task.IsIndeterminate = false;
                task.Value = task.MaxValue;
                task.StopTask();
            }).ConfigureAwait(false);

        return captured;
    }

    private static void ApplyProgress(ProgressTask task, string title, ProgressNotificationValue value)
    {
        if (value.Total is > 0)
        {
            task.IsIndeterminate = false;
            task.MaxValue = value.Total.Value;
            task.Value = value.Progress;
        }

        if (!string.IsNullOrWhiteSpace(value.Message))
        {
            task.Description = Markup.Escape($"{title} - {value.Message}");
        }
    }

    private static bool EscapePressed()
    {
        try
        {
            return Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape;
        }
        catch (InvalidOperationException)
        {
            // stdin redirected (e.g. tests / pipes): no interactive cancel available.
            return false;
        }
    }

    private static async Task RenderInvocationResultAsync(TuiSession session, string title, object report)
    {
        var console = session.Console;
        var hasErrors = InvocationRenderer.HasErrors(report);

        console.WriteLine();
        console.MarkupLine(hasErrors
            ? $"[red]\u2717 {Markup.Escape(title)} (errors)[/]"
            : $"[green]\u2713 {Markup.Escape(title)}[/]");

        switch (report)
        {
            case ToolCallReport tool:
                RenderToolCallResult(console, tool);
                break;
            case ReadReport read:
                RenderReadResult(console, read);
                break;
            case PromptCallReport prompt:
                RenderPromptResult(console, prompt);
                break;
            default:
                // Fallback to the plain-text formatter for any other payload.
                console.WriteLine(TextFormatter.Format(report, App.JsonOptions));
                break;
        }

        RenderServerInitiated(session);
        console.MarkupLine("\n[grey]Press any key to continue...[/]");
        await session.WaitForKey();
    }

    internal static void RenderToolCallResult(IAnsiConsole console, ToolCallReport report)
    {
        if (report.Error is not null)
        {
            RenderResultError(console, report.Error);
            return;
        }

        if (report.Progress.Count > 0)
        {
            console.MarkupLine($"[grey]progress events: {report.Progress.Count}[/]");
        }

        var result = report.Result;
        RenderContentBlocks(console, result?.Content ?? []);
        RenderJsonPanel(console, "structured content", result?.StructuredContent);
        RenderJsonPanel(console, "meta", result?.Meta);

        if ((result?.Content is null || result.Content.Count == 0)
            && result?.StructuredContent is null
            && result?.Meta is null)
        {
            console.MarkupLine("[grey](no content returned)[/]");
        }
    }

    internal static void RenderReadResult(IAnsiConsole console, ReadReport report)
    {
        if (report.Error is not null)
        {
            RenderResultError(console, report.Error);
            return;
        }

        var contents = report.Result?.Contents ?? [];
        if (contents.Count == 0)
        {
            console.MarkupLine("[grey](no contents returned)[/]");
            return;
        }

        foreach (var content in contents)
        {
            var header = content.Uri is { Length: > 0 } uri ? Markup.Escape(uri) : content.Kind;
            var meta = content.MimeType is { Length: > 0 } mime ? $"  [grey54]{Markup.Escape(mime)}[/]" : string.Empty;
            if (!string.IsNullOrEmpty(content.Text))
            {
                RenderTextPanel(console, $"{header}{meta}", content.Text!, content.MimeType);
            }
            else if (content.Raw is not null)
            {
                RenderJsonPanel(console, header, content.Raw);
            }
            else if (content.ByteCount is { } bytes)
            {
                console.MarkupLine($"[grey]{header}: {bytes} byte(s) of binary content[/]");
            }
        }
    }

    internal static void RenderPromptResult(IAnsiConsole console, PromptCallReport report)
    {
        if (report.Error is not null)
        {
            RenderResultError(console, report.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(report.Result?.Description))
        {
            console.MarkupLine($"[grey]description:[/] {Markup.Escape(report.Result!.Description!)}");
        }

        var messages = report.Result?.Messages ?? [];
        if (messages.Count == 0)
        {
            console.MarkupLine("[grey](no messages returned)[/]");
            return;
        }

        foreach (var message in messages)
        {
            var role = message.Role ?? "unknown";
            var roleColour = role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "aqua"
                : role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "green"
                : "grey";
            if (message.Content is { } block)
            {
                if (!string.IsNullOrEmpty(block.Text))
                {
                    RenderTextPanel(console, $"[{roleColour}]{role}[/]", block.Text!, block.MimeType);
                }
                else if (block.Raw is not null)
                {
                    RenderJsonPanel(console, role, block.Raw);
                }
                else
                {
                    console.MarkupLine($"[{roleColour}]{role}[/] [grey]({block.Kind})[/]");
                }
            }
        }
    }

    private static void RenderContentBlocks(IAnsiConsole console, IReadOnlyList<ContentBlockView> content)
    {
        foreach (var block in content)
        {
            if (!string.IsNullOrEmpty(block.Text))
            {
                RenderTextPanel(console, block.Kind, block.Text!, block.MimeType);
            }
            else if (block.Raw is not null)
            {
                RenderJsonPanel(console, block.Kind, block.Raw);
            }
            else if (block.Resource is { } resource)
            {
                var header = resource.Uri is { Length: > 0 } uri ? Markup.Escape(uri) : "embedded resource";
                if (!string.IsNullOrEmpty(resource.Text))
                {
                    RenderTextPanel(console, header, resource.Text!, resource.MimeType);
                }
                else if (resource.Raw is not null)
                {
                    RenderJsonPanel(console, header, resource.Raw);
                }
            }
            else if (block.ByteCount is { } bytes)
            {
                console.MarkupLine($"[grey]{block.Kind}: {bytes} byte(s)[/]");
            }
        }
    }

    private static void RenderResultError(IAnsiConsole console, string error)
    {
        var reason = DescribeConnectionFailure(error);
        console.Write(new Panel($"[red]{Markup.Escape(reason is null ? error : $"{reason}\n{error}")}[/]")
        {
            Header = new PanelHeader(" error "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: Color.Red),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true
        });
    }

    /// <summary>
    /// Renders a JSON node with Spectre's syntax-highlighted <see cref="JsonText"/> widget inside a
    /// titled panel, so structured content / meta / raw blocks are colourised and easy to read.
    /// </summary>
    private static void RenderJsonPanel(IAnsiConsole console, string title, JsonNode? node)
    {
        if (node is null)
        {
            return;
        }

        var json = node.ToJsonString(App.JsonOptions);
        var widget = new JsonText(json)
            .MemberColor(Color.Aqua)
            .StringColor(Color.Green)
            .NumberColor(Color.Yellow)
            .BooleanColor(Color.Orange1)
            .NullColor(Color.Grey);

        console.Write(new Panel(widget)
        {
            Header = new PanelHeader($" {Markup.Escape(title)} "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: Color.Grey),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true
        });
    }

    /// <summary>
    /// Renders a text content block. JSON-ish text (a body that parses as JSON, e.g. a tool that
    /// returns a JSON string) is upgraded to the highlighted JSON panel; otherwise it's shown as a
    /// plain bordered text panel that preserves newlines and never truncates.
    /// </summary>
    private static void RenderTextPanel(IAnsiConsole console, string title, string text, string? mimeType)
    {
        if (LooksLikeJson(text, mimeType) && TryParseJson(text) is { } parsed)
        {
            RenderJsonPanel(console, title, parsed);
            return;
        }

        console.Write(new Panel(new Text(text))
        {
            Header = new PanelHeader($" {Markup.Escape(title)} "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: Color.Grey),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true
        });
    }

    private static bool LooksLikeJson(string text, string? mimeType)
    {
        if (mimeType is not null && mimeType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var trimmed = text.AsSpan().Trim();
        return trimmed.Length > 1 && (trimmed[0] is '{' or '[');
    }

    private static JsonNode? TryParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Shows the server-initiated traffic captured during (or since) the invocation: sampling /
    /// elicitation / roots requests the server made back at us, plus any notifications. Nothing is
    /// rendered when the server stayed quiet, so the common case is unchanged.
    /// </summary>
    private static void RenderServerInitiated(TuiSession session)
    {
        var captured = session.Interaction?.Drain();
        if (captured is null || captured.Count == 0)
        {
            return;
        }

        var console = session.Console;
        var table = new Table().RoundedBorder().BorderColor(Color.Purple);
        table.Title = new TableTitle("server-initiated");
        table.AddColumn("Method");
        table.AddColumn("Detail");
        table.AddColumn("Our response");
        foreach (var item in captured)
        {
            table.AddRow(
                Markup.Escape(item.Method),
                Markup.Escape(item.Detail),
                Markup.Escape(item.Response ?? "-"));
        }

        console.WriteLine();
        console.Write(table);
    }

    private static async Task ShowBookmarksAsync(
        IAnsiConsole console,
        ServerInspection server,
        TuiBookmarkStore bookmarks,
        Func<Task> waitForKey)
    {
        console.Clear();
        RenderServerSummary(console, server);
        var items = bookmarks.ForServer(server.Name);

        if (items.Count == 0)
        {
            console.MarkupLine("\n[grey]No bookmarks yet. Open a tool/resource/prompt and press 'Bookmark'.[/]");
            console.MarkupLine("[grey]Press any key to continue...[/]");
            await waitForKey();
            return;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("Kind");
        table.AddColumn("Name");
        foreach (var bookmark in items)
        {
            table.AddRow(Markup.Escape(bookmark.Kind.ToString()), Markup.Escape(bookmark.Name));
        }
        console.Write(table);

        console.MarkupLine("\n[grey]Press any key to continue...[/]");
        await waitForKey();
    }

    private static async Task ToggleBookmarkAsync(TuiSession session, TuiBookmark bookmark)
    {
        var existed = session.Bookmarks.Contains(bookmark);
        session.Bookmarks.Toggle(bookmark);
        session.Console.MarkupLine(existed ? "[grey]Removed bookmark.[/]" : "[green]Bookmarked.[/]");
        session.Console.MarkupLine("[grey]Press any key to continue...[/]");
        await session.WaitForKey();
    }

    // --- Choice helpers --------------------------------------------------

    private const string CallChoice = "Call tool";
    private const string ReadChoice = "Read";
    private const string GetPromptChoice = "Get prompt";

    private static string PromptFilter(IAnsiConsole console)
        => console.Prompt(new TextPrompt<string>("Filter (substring, case-insensitive):").AllowEmpty());

    // --- Item display formatters (one line per row in the selection menu) -

    private static string ToolDisplay(ToolInfo tool)
        => string.IsNullOrWhiteSpace(tool.Description)
            ? tool.Name
            : $"{tool.Name}   —   {Collapse(tool.Description!)}";

    private static string ResourceDisplay(ResourceInfo resource)
    {
        var name = resource.Name ?? resource.Uri ?? "(unnamed)";
        var parts = new List<string> { name };
        if (!string.IsNullOrWhiteSpace(resource.Uri) && resource.Uri != name)
        {
            parts.Add(resource.Uri!);
        }
        if (!string.IsNullOrWhiteSpace(resource.MimeType))
        {
            parts.Add($"[{resource.MimeType}]");
        }
        return string.Join("   ", parts);
    }

    private static string ResourceTemplateDisplay(ResourceTemplateInfo template)
    {
        var name = template.Name ?? template.UriTemplate ?? "(unnamed)";
        var parts = new List<string> { name };
        if (!string.IsNullOrWhiteSpace(template.UriTemplate) && template.UriTemplate != name)
        {
            parts.Add(template.UriTemplate!);
        }
        if (!string.IsNullOrWhiteSpace(template.MimeType))
        {
            parts.Add($"[{template.MimeType}]");
        }
        return string.Join("   ", parts);
    }

    private static string PromptDisplay(PromptInfo prompt)
    {
        var line = prompt.Name;
        if (!string.IsNullOrWhiteSpace(prompt.Description))
        {
            line += $"   —   {Collapse(prompt.Description!)}";
        }

        var arguments = string.Join(", ", prompt.Arguments
            .Select(argument => argument.Required ? $"{argument.Name}*" : argument.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)));
        if (arguments.Length > 0)
        {
            line += $"   ({arguments})";
        }
        return line;
    }

    /// <summary>Flattens a (possibly multi-line) description to a single trimmed, length-capped line.</summary>
    private static string Collapse(string text)
    {
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 80 ? oneLine : $"{oneLine[..79]}…";
    }

    // --- Live "selected item" detail (fixed-height, scrollable, under the list) -----
    //
    // The list row is one line (long descriptions are Collapse()d to fit); these build the FULL,
    // untruncated description + metadata as markup lines. TuiMenu renders them in a fixed-height
    // pane and scrolls within it, so the layout never jumps between items of different length.

    internal static TuiDetail BuildToolListDetail(ToolInfo tool)
    {
        var lines = new List<TuiDetailLine>
        {
            string.IsNullOrWhiteSpace(tool.Description)
                ? new TuiDetailLine("(no description)", "grey35")
                : new TuiDetailLine(tool.Description!.Trim(), "grey")
        };
        if (tool.InputSchema is not null)
        {
            lines.Add(new TuiDetailLine("enter for input schema", "grey35"));
        }
        return new TuiDetail($"selected: [aqua]{Markup.Escape(tool.Name)}[/]", Color.Aqua, lines);
    }

    internal static TuiDetail BuildPromptListDetail(PromptInfo prompt)
    {
        var lines = new List<TuiDetailLine>
        {
            string.IsNullOrWhiteSpace(prompt.Description)
                ? new TuiDetailLine("(no description)", "grey35")
                : new TuiDetailLine(prompt.Description!.Trim(), "grey")
        };
        if (prompt.Arguments.Count > 0)
        {
            var args = prompt.Arguments.Select(a => a.Required ? $"{a.Name}*" : a.Name);
            lines.Add(new TuiDetailLine($"args: {string.Join(", ", args)}", "grey54"));
        }
        return new TuiDetail($"selected: [magenta]{Markup.Escape(prompt.Name)}[/]", Color.Magenta1, lines);
    }

    internal static TuiDetail BuildResourceListDetail(ResourceInfo resource)
    {
        var lines = new List<TuiDetailLine>();
        if (!string.IsNullOrWhiteSpace(resource.Uri)) lines.Add(new TuiDetailLine($"uri: {resource.Uri}", "green"));
        if (!string.IsNullOrWhiteSpace(resource.MimeType)) lines.Add(new TuiDetailLine($"mime: {resource.MimeType}", "grey54"));
        lines.Add(string.IsNullOrWhiteSpace(resource.Description)
            ? new TuiDetailLine("(no description)", "grey35")
            : new TuiDetailLine(resource.Description!.Trim(), "grey"));
        var name = resource.Name ?? resource.Uri ?? "(unnamed)";
        return new TuiDetail($"selected: [green]{Markup.Escape(name)}[/]", Color.Green, lines);
    }

    internal static TuiDetail BuildResourceTemplateListDetail(ResourceTemplateInfo template)
    {
        var lines = new List<TuiDetailLine>();
        if (!string.IsNullOrWhiteSpace(template.UriTemplate)) lines.Add(new TuiDetailLine($"template: {template.UriTemplate}", "green"));
        if (!string.IsNullOrWhiteSpace(template.MimeType)) lines.Add(new TuiDetailLine($"mime: {template.MimeType}", "grey54"));
        lines.Add(string.IsNullOrWhiteSpace(template.Description)
            ? new TuiDetailLine("(no description)", "grey35")
            : new TuiDetailLine(template.Description!.Trim(), "grey"));
        var name = template.Name ?? template.UriTemplate ?? "(unnamed)";
        return new TuiDetail($"selected: [green]{Markup.Escape(name)}[/]", Color.Green, lines);
    }

    // --- Filters ---------------------------------------------------------

    internal static IReadOnlyList<ToolInfo> FilterTools(IReadOnlyList<ToolInfo> items, string filter)
        => string.IsNullOrEmpty(filter)
            ? items
            : items.Where(t =>
                t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (t.Description ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

    internal static IReadOnlyList<ResourceInfo> FilterResources(IReadOnlyList<ResourceInfo> items, string filter)
        => string.IsNullOrEmpty(filter)
            ? items
            : items.Where(r =>
                (r.Name ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (r.Uri ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (r.Description ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

    internal static IReadOnlyList<ResourceTemplateInfo> FilterResourceTemplates(IReadOnlyList<ResourceTemplateInfo> items, string filter)
        => string.IsNullOrEmpty(filter)
            ? items
            : items.Where(r =>
                (r.Name ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (r.UriTemplate ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (r.Description ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

    internal static IReadOnlyList<PromptInfo> FilterPrompts(IReadOnlyList<PromptInfo> items, string filter)
        => string.IsNullOrEmpty(filter)
            ? items
            : items.Where(p =>
                p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (p.Description ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

    // --- Existing renders (unchanged signatures, preserved for tests) ----

    internal static void RenderServerSummary(IAnsiConsole console, ServerInspection server)
    {
        var reachable = server.Error is null;
        var statusDot = reachable ? "[green]\u25cf[/]" : "[red]\u25cf[/]";
        var body = $"{statusDot} [bold]{Markup.Escape(server.Name)}[/]  [grey]\u2502[/]  [grey54]{Markup.Escape(server.Transport)}[/]"
                   + $"\n  [grey]{Markup.Escape(server.Target)}[/]";
        if (!reachable)
        {
            var reason = DescribeConnectionFailure(server.Error);
            var headline = reason is null ? "connection failed" : $"connection failed: {reason}";
            body += $"\n  [red]{Markup.Escape(headline)}[/]";
            // Keep the raw exception text underneath for the full detail when it adds information
            // beyond the distilled reason.
            if (reason is null || !server.Error!.Contains(reason, StringComparison.OrdinalIgnoreCase))
            {
                body += $"\n  [grey]{Markup.Escape(server.Error!)}[/]";
            }
        }
        else if (TextFormatter.DescribeConnectionAuth(server.AuthStatus) is { } authLine)
        {
            var authenticated = server.AuthStatus!.Mode == ConnectionAuthModes.Authenticated;
            var colour = authenticated ? "green" : "grey";
            body += $"\n  [{colour}]auth: {Markup.Escape(authLine)}[/]";
        }

        var panel = new Panel(body)
        {
            Header = new PanelHeader($" {server.Transport} server "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: reachable ? Color.Grey : Color.Red),
            Padding = new Padding(1, 0, 1, 0)
        };

        console.Write(panel);
    }

    /// <summary>
    /// Compact, always-visible section counts + declared capabilities rendered just under the
    /// server summary panel (above the menu) so the user sees "how much does this server expose"
    /// and "what does it advertise" without opening Overview. Counts use a coloured dot
    /// (green = has items, grey = empty, red = section errored); capabilities are chips that are
    /// bright when declared and dimmed/struck when the server doesn't advertise them.
    /// </summary>
    internal static void RenderSectionCountsBar(IAnsiConsole console, ServerInspection server)
    {
        if (server.Error is not null)
        {
            console.MarkupLine("  [red]\u25cf unreachable[/] [grey]- counts unavailable until the server responds[/]");
            return;
        }

        var counts = new[]
        {
            FormatSectionCount(server.Tools, "tool"),
            FormatSectionCount(server.Prompts, "prompt"),
            FormatSectionCount(server.Resources, "resource"),
            FormatSectionCount(server.ResourceTemplates, "template")
        };
        console.MarkupLine("  " + string.Join("   ", counts));
        console.MarkupLine("  [grey]caps[/] " + FormatCapabilityChips(server.Capabilities));
    }

    /// <summary>
    /// Renders every capability flag as a chip: bright + a filled dot when the server declares it,
    /// dim + a hollow dot when it doesn't. Mirrors the "Capabilities" row of the Overview table.
    /// </summary>
    private static string FormatCapabilityChips(CapabilitySnapshot capabilities)
    {
        var chips = new[]
        {
            Chip("tools", capabilities.Tools),
            Chip("resources", capabilities.Resources),
            Chip("prompts", capabilities.Prompts),
            Chip("logging", capabilities.Logging),
            Chip("completions", capabilities.Completions)
        };
        return string.Join("  ", chips);

        static string Chip(string name, bool present) => present
            ? $"[green]\u25cf[/] [white]{name}[/]"
            : $"[grey35]\u25cb[/] [grey35]{name}[/]";
    }

    private static string FormatSectionCount<T>(SectionResult<T> section, string noun)
    {
        var count = section.Items.Count;
        string dot;
        if (section.Error is not null)
        {
            dot = "[red]\u25cf[/]";
        }
        else if (!section.Supported)
        {
            dot = "[grey35]\u25cb[/]"; // hollow: capability not offered at all
        }
        else
        {
            dot = count > 0 ? "[green]\u25cf[/]" : "[grey]\u25cb[/]";
        }

        var label = count == 1 ? noun : noun + "s";
        var countColour = section.Error is not null ? "red" : count > 0 ? "white" : "grey";
        return $"{dot} [{countColour}]{count}[/] [grey]{label}[/]";
    }

    internal static void RenderOverview(IAnsiConsole console, ServerInspection server)
    {
        if (TryRenderConnectionError(console, server))
        {
            return;
        }

        var table = new Table()
            .RoundedBorder()
            .BorderColor(Color.Grey)
            .Title("[bold]capabilities & counts[/]");
        table.AddColumn("[grey]Section[/]");
        table.AddColumn("[grey]Status[/]");
        table.AddColumn(new TableColumn("[grey]Count[/]").RightAligned());
        table.AddRow("Capabilities", $"[aqua]{Markup.Escape(FormatCapabilities(server.Capabilities))}[/]", "[grey]-[/]");
        table.AddRow("Tools", ColourSectionStatus(server.Tools), CountCell(server.Tools));
        table.AddRow("Resources", ColourSectionStatus(server.Resources), CountCell(server.Resources));
        table.AddRow("Resource Templates", ColourSectionStatus(server.ResourceTemplates), CountCell(server.ResourceTemplates));
        table.AddRow("Prompts", ColourSectionStatus(server.Prompts), CountCell(server.Prompts));
        console.Write(table);
    }

    private static string CountCell<T>(SectionResult<T> section)
    {
        var count = section.Items.Count;
        return count > 0 ? $"[white]{count}[/]" : "[grey]0[/]";
    }

    /// <summary>Coloured variant of <see cref="SectionStatus{T}"/> for the overview table.</summary>
    private static string ColourSectionStatus<T>(SectionResult<T> section)
    {
        if (section.Error is not null)
        {
            return $"[red]error: {Markup.Escape(section.Error)}[/]";
        }
        return section.Supported ? "[green]ok[/]" : "[grey]not supported[/]";
    }

    /// <summary>
    /// When the whole inspection failed (the connection/handshake never succeeded), the
    /// per-section <see cref="SectionResult{T}.Error"/> fields stay null and the items lists are
    /// empty - which on its own reads as "this server exposes nothing". That is misleading: we
    /// never actually learned what the server exposes. This surfaces the server-level
    /// <see cref="ServerInspection.Error"/> so a failed connect is never silently shown as an
    /// empty-but-healthy server. Returns true when an error was rendered (caller should stop).
    /// </summary>
    private static bool TryRenderConnectionError(IAnsiConsole console, ServerInspection server)
    {
        if (server.Error is null)
        {
            return false;
        }

        var reason = DescribeConnectionFailure(server.Error);
        var headline = reason is null ? "Connection failed" : $"Connection failed: {reason}";
        console.MarkupLine($"[red]{Markup.Escape(headline)}[/]");
        if (reason is null || !server.Error.Contains(reason, StringComparison.OrdinalIgnoreCase))
        {
            console.MarkupLine($"[grey]{Markup.Escape(server.Error)}[/]");
        }
        console.MarkupLine("[grey]The server could not be inspected, so nothing it exposes is available.[/]");
        return true;
    }

    internal static void RenderToolDetail(IAnsiConsole console, ToolInfo tool)
    {
        console.Write(DetailPanel(
            "tool",
            $"[bold aqua]{Markup.Escape(tool.Name)}[/]\n[grey]{Markup.Escape(tool.Description ?? "(no description)")}[/]",
            Color.Aqua));

        if (tool.InputSchema is null)
        {
            console.MarkupLine("[grey]No input schema declared.[/]");
            return;
        }

        var root = new Tree("[bold]inputSchema[/]").Style(Style.Parse("grey"));
        AppendSchemaNode(root, tool.InputSchema);
        console.Write(root);
    }

    internal static void RenderResourceDetail(IAnsiConsole console, ResourceInfo resource)
    {
        var body = $"[bold green]{Markup.Escape(resource.Name ?? "(unnamed)")}[/]\n[grey]{Markup.Escape(resource.Uri ?? "(no uri)")}[/]";
        if (!string.IsNullOrWhiteSpace(resource.MimeType))
        {
            body += $"\n[grey]mime: {Markup.Escape(resource.MimeType!)}[/]";
        }
        if (!string.IsNullOrWhiteSpace(resource.Description))
        {
            body += $"\n{Markup.Escape(resource.Description!)}";
        }
        console.Write(DetailPanel("resource", body, Color.Green));
    }

    internal static void RenderResourceTemplateDetail(IAnsiConsole console, ResourceTemplateInfo template)
    {
        var body = $"[bold green]{Markup.Escape(template.Name ?? "(unnamed)")}[/]\n[grey]{Markup.Escape(template.UriTemplate ?? "(no template)")}[/]";
        if (!string.IsNullOrWhiteSpace(template.MimeType))
        {
            body += $"\n[grey]mime: {Markup.Escape(template.MimeType!)}[/]";
        }
        if (!string.IsNullOrWhiteSpace(template.Description))
        {
            body += $"\n{Markup.Escape(template.Description!)}";
        }

        console.Write(DetailPanel("resource template", body, Color.Green));

        var variables = ArgumentElicitor.ExtractTemplateVariables(template.UriTemplate ?? string.Empty);
        if (variables.Count > 0)
        {
            console.MarkupLine($"[grey]variables:[/] [yellow]{Markup.Escape(string.Join(", ", variables))}[/]");
        }
    }

    internal static void RenderPromptDetail(IAnsiConsole console, PromptInfo prompt)
    {
        console.Write(DetailPanel(
            "prompt",
            $"[bold magenta]{Markup.Escape(prompt.Name)}[/]\n[grey]{Markup.Escape(prompt.Description ?? "(no description)")}[/]",
            Color.Magenta1));

        if (prompt.Arguments.Count == 0)
        {
            console.MarkupLine("[grey]No arguments declared.[/]");
            return;
        }

        var table = new Table().RoundedBorder().BorderColor(Color.Grey);
        table.AddColumn("[grey]Argument[/]");
        table.AddColumn("[grey]Required[/]");
        table.AddColumn("[grey]Description[/]");
        foreach (var argument in prompt.Arguments)
        {
            table.AddRow(
                $"[white]{Markup.Escape(argument.Name ?? "(unnamed)")}[/]",
                argument.Required ? "[red]required[/]" : "[grey]optional[/]",
                Markup.Escape(argument.Description ?? string.Empty));
        }
        console.Write(table);
    }

    /// <summary>Shared rounded, coloured detail panel so every drill-in screen looks consistent.</summary>
    private static Panel DetailPanel(string header, string body, Color accent)
        => new(body)
        {
            Header = new PanelHeader($" {header} "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: accent),
            Padding = new Padding(1, 0, 1, 0)
        };

    private static void AppendSchemaNode(IHasTreeNodes parent, JsonNode? node)
    {
        if (node is null)
        {
            parent.AddNode("[grey](null)[/]");
            return;
        }

        if (node is JsonObject obj)
        {
            // Recognise the common JSON-Schema "properties" shape and expand it nicely:
            // each top-level property becomes its own subtree with type + required + description.
            var type = obj["type"]?.GetValue<string>();
            var properties = obj["properties"] as JsonObject;
            var required = (obj["required"] as JsonArray)?.OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();

            if (type is not null)
            {
                parent.AddNode($"[blue]type[/]: {Markup.Escape(type)}");
            }

            if (properties is not null)
            {
                var propsNode = parent.AddNode("[blue]properties[/]");
                foreach (var (name, value) in properties)
                {
                    var requiredMark = required.Contains(name) ? " [red]*[/]" : string.Empty;
                    var propType = (value as JsonObject)?["type"]?.GetValue<string>() ?? "any";
                    var description = (value as JsonObject)?["description"]?.GetValue<string>();
                    var label = $"[green]{Markup.Escape(name)}[/]{requiredMark} [grey]({Markup.Escape(propType)})[/]";
                    var propNode = propsNode.AddNode(label);
                    if (!string.IsNullOrEmpty(description))
                    {
                        propNode.AddNode($"[grey]{Markup.Escape(description)}[/]");
                    }
                }
            }
            else
            {
                // Not a "properties" object - just dump the top-level keys for orientation.
                foreach (var (key, value) in obj)
                {
                    if (key is "type") continue;
                    parent.AddNode($"[blue]{Markup.Escape(key)}[/]: {Markup.Escape(JsonDescribe(value))}");
                }
            }
            return;
        }

        parent.AddNode(Markup.Escape(JsonDescribe(node)));
    }

    private static string JsonDescribe(JsonNode? node) => node switch
    {
        null => "(null)",
        JsonValue v => v.ToJsonString(),
        JsonArray a => $"array[{a.Count}]",
        JsonObject o => $"object{{{o.Count} keys}}",
        _ => node.ToJsonString()
    };

    internal static void RenderFilterHeader(IAnsiConsole console, string filter, int shown, int total)
    {
        if (string.IsNullOrEmpty(filter))
        {
            console.MarkupLine($"[grey]{total} item(s)[/]");
            return;
        }
        console.MarkupLine($"[grey]filter:[/] [yellow]{Markup.Escape(filter)}[/] [grey]({shown}/{total} match)[/]");
    }

    internal static string SectionStatus<T>(SectionResult<T> section)
        => section.Error is not null ? $"error: {section.Error}" : section.Supported ? "ok" : "not supported";

    /// <summary>
    /// Distils a verbose connection <see cref="ServerInspection.Error"/> (e.g.
    /// <c>"HttpRequestException: Response status code does not indicate success: 401 (Unauthorized)."</c>)
    /// into a short, scannable reason such as <c>"401 Unauthorized"</c>, <c>"404 Not Found"</c>,
    /// <c>"timed out"</c>, or <c>"connection refused"</c>. The aim is to answer "why is this server
    /// unreachable?" at a glance - the full <see cref="ServerInspection.Error"/> is still shown in the
    /// server summary panel for the complete detail. Returns null when there is no error.
    /// </summary>
    internal static string? DescribeConnectionFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        // Transport-level failures carry no HTTP status and must be matched first - otherwise a
        // port number or address fragment (e.g. "host:443") could be mistaken for a status code.
        if (error.Contains("Timed out", StringComparison.OrdinalIgnoreCase)
            || error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "timed out";
        }
        if (error.Contains("refused", StringComparison.OrdinalIgnoreCase))
        {
            return "connection refused";
        }
        if (error.Contains("no such host", StringComparison.OrdinalIgnoreCase)
            || error.Contains("name or service not known", StringComparison.OrdinalIgnoreCase)
            || error.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase))
        {
            return "host not found";
        }
        if (error.Contains("connection was closed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || error.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase))
        {
            return "connection dropped";
        }
        if (error.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || error.Contains("TLS", StringComparison.OrdinalIgnoreCase)
            || error.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return "TLS error";
        }

        // An HTTP status code is the most actionable signal: surface "<code> <reason>" when present.
        // Match only in genuine status contexts (the .NET HttpRequestException phrasing, an explicit
        // "status code"/"HTTP" prefix, or "<code> (Reason)") so address/port digits aren't misread.
        var status = System.Text.RegularExpressions.Regex.Match(
            error,
            @"(?:status code(?:\s+does not indicate success)?:?\s*|HTTP[/ ]?(?:\d(?:\.\d)?\s+)?|\bstatus\s+|\breturned\s+)([1-5]\d{2})\b(?:\s*\(([^)]+)\))?"
                + @"|\b([1-5]\d{2})\s*\(([^)]+)\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (status.Success)
        {
            var code = status.Groups[1].Success ? status.Groups[1].Value : status.Groups[3].Value;
            var phraseGroup = status.Groups[1].Success ? status.Groups[2] : status.Groups[4];
            var phrase = phraseGroup.Success ? phraseGroup.Value.Trim() : DefaultReasonPhrase(code);
            return string.IsNullOrEmpty(phrase) ? code : $"{code} {phrase}";
        }

        // Fall back to the exception type when the message is just "TypeName: details".
        var colon = error.IndexOf(':');
        if (colon > 0)
        {
            var typeName = error[..colon].Trim();
            if (typeName.EndsWith("Exception", StringComparison.Ordinal))
            {
                return typeName;
            }
        }

        return null;
    }

    private static string DefaultReasonPhrase(string code) => code switch
    {
        "401" => "Unauthorized",
        "403" => "Forbidden",
        "404" => "Not Found",
        "405" => "Method Not Allowed",
        "406" => "Not Acceptable",
        "408" => "Request Timeout",
        "410" => "Gone",
        "429" => "Too Many Requests",
        "500" => "Internal Server Error",
        "502" => "Bad Gateway",
        "503" => "Service Unavailable",
        "504" => "Gateway Timeout",
        _ => string.Empty
    };

    internal static string FormatCapabilities(CapabilitySnapshot capabilities)
    {
        var names = new List<string>();
        if (capabilities.Tools) names.Add("tools");
        if (capabilities.Resources) names.Add("resources");
        if (capabilities.Prompts) names.Add("prompts");
        if (capabilities.Logging) names.Add("logging");
        if (capabilities.Completions) names.Add("completions");
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static Task DefaultWaitForKey()
    {
        Console.ReadKey(true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Per-render-loop dependencies threaded through the navigation methods: the console, the
    /// "press a key" hook (swappable in tests), the bookmark store, and the optional
    /// <see cref="Connector"/> that turns the explorer into an interactive caller. When
    /// <see cref="Connector"/> is null (library hosts / unit tests) the Call/Read/Get actions are
    /// not offered. <see cref="Mcp"/> holds the live session for the current server, opened lazily
    /// on first invoke and closed when leaving the server.
    /// </summary>
    private sealed class TuiSession(
        IAnsiConsole console,
        Func<Task> waitForKey,
        TuiBookmarkStore bookmarks,
        McpSessionConnector? connector,
        TuiServerInteraction? interaction = null)
    {
        public IAnsiConsole Console { get; } = console;
        public Func<Task> WaitForKey { get; } = waitForKey;
        public TuiBookmarkStore Bookmarks { get; } = bookmarks;
        public McpSessionConnector? Connector { get; } = connector;
        public TuiServerInteraction? Interaction { get; } = interaction;
        public IMcpSession? Mcp { get; set; }

        /// <summary>The log level currently requested from the server (null until logging is enabled).</summary>
        public ModelContextProtocol.Protocol.LoggingLevel? LogLevel { get; set; }

        public async Task CloseSessionAsync()
        {
            if (Mcp is not null)
            {
                await Mcp.DisposeAsync();
                Mcp = null;
            }

            LogLevel = null;
        }
    }
}
