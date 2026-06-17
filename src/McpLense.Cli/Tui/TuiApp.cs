using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using Spectre.Console;

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

        while (true)
        {
            var serverItems = servers
                .Select(server =>
                {
                    var label = $"{server.Name}   [{server.Transport}]   {server.Target}";
                    return server.Error is not null ? $"{label}   (unreachable)" : label;
                })
                .ToArray();

            var result = TuiMenu.Select(
                console,
                renderHeader: null,
                title: "Select an MCP server",
                items: serverItems,
                options: new TuiMenuOptions { ExitLabel = "Exit" });

            if (result.Action is not TuiMenuAction.Item)
            {
                return 0;
            }

            await ShowServerAsync(session, servers[result.Index]);
        }
    }

    private static async Task ShowServerAsync(TuiSession session, ServerInspection server)
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
            await RunServerLoopAsync(session, server, filters);
        }
        finally
        {
            // A live invocation session is opened lazily on first invoke; close it when leaving
            // the server so we don't hold the transport (or keep a stdio process up) afterwards.
            await session.CloseSessionAsync();
        }
    }

    private static async Task RunServerLoopAsync(TuiSession session, ServerInspection server, IDictionary<string, string> filters)
    {
        var console = session.Console;

        while (true)
        {
            var bookmarksForServer = session.Bookmarks.ForServer(server.Name);
            var bookmarksLabel = bookmarksForServer.Count == 0
                ? "Bookmarks"
                : $"Bookmarks ({bookmarksForServer.Count})";

            var sections = new[] { "Overview", "Tools", "Resources", "Resource Templates", "Prompts", bookmarksLabel };

            var result = TuiMenu.Select(
                console,
                renderHeader: () => RenderServerSummary(console, server),
                title: "Choose a section",
                items: sections,
                options: new TuiMenuOptions { BackLabel = "Back to servers" });

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
            tool => ShowToolDetailAsync(session, server, tool));

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
            resource => ShowResourceDetailAsync(session, server, resource));

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
            template => ShowResourceTemplateDetailAsync(session, server, template));

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
            prompt => ShowPromptDetailAsync(session, server, prompt));

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
        Func<T, Task> drill)
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
                });

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
        await RenderInvocationResultAsync(session, $"call {tool.Name}", InvocationRenderer.Render(report));
    }

    private static async Task InvokeResourceAsync(TuiSession session, ServerInspection server, string uri)
    {
        var mcp = await EnsureSessionAsync(session, server);
        if (mcp is null) return;

        if (!ConfirmRun(session, "read", uri, arguments: null, server)) return;

        var report = await mcp.ReadResourceAsync(uri, arguments: null, CancellationToken.None);
        await RenderInvocationResultAsync(session, $"read {uri}", InvocationRenderer.Render(report));
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
        await RenderInvocationResultAsync(session, $"read {uriTemplate}", InvocationRenderer.Render(report));
    }

    private static async Task InvokePromptAsync(TuiSession session, ServerInspection server, PromptInfo prompt)
    {
        var mcp = await EnsureSessionAsync(session, server);
        if (mcp is null) return;

        var arguments = await ArgumentElicitor.ElicitPromptArgumentsAsync(
            session.Console, prompt.Arguments, new PromptCompletionSource(mcp, prompt.Name));

        if (!ConfirmRun(session, "prompt", prompt.Name, arguments, server)) return;

        var report = await mcp.GetPromptAsync(prompt.Name, arguments, CancellationToken.None);
        await RenderInvocationResultAsync(session, $"prompt {prompt.Name}", InvocationRenderer.Render(report));
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

    private static async Task RenderInvocationResultAsync(TuiSession session, string title, InvokeResult result)
    {
        var console = session.Console;
        console.WriteLine();
        console.MarkupLine(result.HasErrors
            ? $"[red]x {Markup.Escape(title)} (errors)[/]"
            : $"[green]+ {Markup.Escape(title)}[/]");
        console.WriteLine(result.Text);
        RenderServerInitiated(session);
        console.MarkupLine("\n[grey]Press any key to continue...[/]");
        await session.WaitForKey();
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
        var body = $"[bold]{Markup.Escape(server.Name)}[/]\n[grey]{Markup.Escape(server.Target)}[/]";
        if (server.Error is not null)
        {
            body += $"\n[red]connection failed: {Markup.Escape(server.Error)}[/]";
        }
        else if (TextFormatter.DescribeConnectionAuth(server.AuthStatus) is { } authLine)
        {
            var colour = server.AuthStatus!.Mode == ConnectionAuthModes.Authenticated ? "green" : "grey";
            body += $"\n[{colour}]auth: {Markup.Escape(authLine)}[/]";
        }

        var panel = new Panel(body)
        {
            Header = new PanelHeader($"{server.Transport} server")
        };
        if (server.Error is not null)
        {
            panel.BorderStyle = new Style(foreground: Color.Red);
        }

        console.Write(panel);
    }

    internal static void RenderOverview(IAnsiConsole console, ServerInspection server)
    {
        if (TryRenderConnectionError(console, server))
        {
            return;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("Section");
        table.AddColumn("Status");
        table.AddColumn("Count");
        table.AddRow("Capabilities", Markup.Escape(FormatCapabilities(server.Capabilities)), "-");
        table.AddRow("Tools", SectionStatus(server.Tools), server.Tools.Items.Count.ToString());
        table.AddRow("Resources", SectionStatus(server.Resources), server.Resources.Items.Count.ToString());
        table.AddRow("Resource Templates", SectionStatus(server.ResourceTemplates), server.ResourceTemplates.Items.Count.ToString());
        table.AddRow("Prompts", SectionStatus(server.Prompts), server.Prompts.Items.Count.ToString());
        console.Write(table);
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

        console.MarkupLine($"[red]Connection failed: {Markup.Escape(server.Error)}[/]");
        console.MarkupLine("[grey]The server could not be inspected, so nothing it exposes is available.[/]");
        return true;
    }

    internal static void RenderToolDetail(IAnsiConsole console, ToolInfo tool)
    {
        console.Write(new Panel($"[bold]{Markup.Escape(tool.Name)}[/]\n[grey]{Markup.Escape(tool.Description ?? string.Empty)}[/]")
        {
            Header = new PanelHeader("Tool")
        });

        if (tool.InputSchema is null)
        {
            console.MarkupLine("[grey]No input schema declared.[/]");
            return;
        }

        var root = new Tree("[bold]inputSchema[/]");
        AppendSchemaNode(root, tool.InputSchema);
        console.Write(root);
    }

    internal static void RenderResourceDetail(IAnsiConsole console, ResourceInfo resource)
    {
        var body = $"[bold]{Markup.Escape(resource.Name ?? "(unnamed)")}[/]\n[grey]{Markup.Escape(resource.Uri ?? "(no uri)")}[/]";
        if (!string.IsNullOrWhiteSpace(resource.MimeType))
        {
            body += $"\n[grey]mime: {Markup.Escape(resource.MimeType!)}[/]";
        }
        if (!string.IsNullOrWhiteSpace(resource.Description))
        {
            body += $"\n{Markup.Escape(resource.Description!)}";
        }
        console.Write(new Panel(body) { Header = new PanelHeader("Resource") });
    }

    internal static void RenderResourceTemplateDetail(IAnsiConsole console, ResourceTemplateInfo template)
    {
        var body = $"[bold]{Markup.Escape(template.Name ?? "(unnamed)")}[/]\n[grey]{Markup.Escape(template.UriTemplate ?? "(no template)")}[/]";
        if (!string.IsNullOrWhiteSpace(template.MimeType))
        {
            body += $"\n[grey]mime: {Markup.Escape(template.MimeType!)}[/]";
        }
        if (!string.IsNullOrWhiteSpace(template.Description))
        {
            body += $"\n{Markup.Escape(template.Description!)}";
        }

        console.Write(new Panel(body) { Header = new PanelHeader("Resource template") });

        var variables = ArgumentElicitor.ExtractTemplateVariables(template.UriTemplate ?? string.Empty);
        if (variables.Count > 0)
        {
            console.MarkupLine($"[grey]variables:[/] {Markup.Escape(string.Join(", ", variables))}");
        }
    }

    internal static void RenderPromptDetail(IAnsiConsole console, PromptInfo prompt)
    {
        console.Write(new Panel($"[bold]{Markup.Escape(prompt.Name)}[/]\n[grey]{Markup.Escape(prompt.Description ?? string.Empty)}[/]")
        {
            Header = new PanelHeader("Prompt")
        });

        if (prompt.Arguments.Count == 0)
        {
            console.MarkupLine("[grey]No arguments declared.[/]");
            return;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("Argument");
        table.AddColumn("Required");
        table.AddColumn("Description");
        foreach (var argument in prompt.Arguments)
        {
            table.AddRow(
                Markup.Escape(argument.Name ?? "(unnamed)"),
                argument.Required ? "yes" : "no",
                Markup.Escape(argument.Description ?? string.Empty));
        }
        console.Write(table);
    }

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

        public async Task CloseSessionAsync()
        {
            if (Mcp is not null)
            {
                await Mcp.DisposeAsync();
                Mcp = null;
            }
        }
    }
}
