using System.Text.Json;
using System.Text.Json.Nodes;
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

        // Re-drive the same parsed command for invocations so tool calls / reads / prompt
        // fetches authenticate exactly the way the equivalent CLI command would.
        var invoker = new McpExecutorInvoker(command, multiServer: report.Servers.Count > 1);
        return await RenderAsync(report, console, waitForKey, bookmarkStore, invoker);
    }

    internal static Task<int> RenderAsync(
        InspectReport report,
        IAnsiConsole console,
        Func<Task> waitForKey)
        => RenderAsync(report, console, waitForKey, bookmarkStore: null, invoker: null);

    internal static Task<int> RenderAsync(
        InspectReport report,
        IAnsiConsole console,
        Func<Task> waitForKey,
        TuiBookmarkStore? bookmarkStore)
        => RenderAsync(report, console, waitForKey, bookmarkStore, invoker: null);

    internal static async Task<int> RenderAsync(
        InspectReport report,
        IAnsiConsole console,
        Func<Task> waitForKey,
        TuiBookmarkStore? bookmarkStore,
        IMcpInvoker? invoker)
    {
        var servers = report.Servers;
        if (servers.Count == 0)
        {
            console.MarkupLine("[red]No servers were resolved.[/]");
            return 1;
        }

        bookmarkStore ??= TuiBookmarkStore.InMemory();
        var session = new TuiSession(console, waitForKey, bookmarkStore, invoker);

        while (true)
        {
            console.Clear();
            var serverOptions = servers
                .Select((server, index) => new { Label = Markup.Escape($"{index + 1}. {server.Name} [{server.Transport}] {server.Target}"), Server = server })
                .ToArray();

            var selectedServerLabel = console.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select an MCP server")
                    .PageSize(10)
                    .AddChoices(serverOptions.Select(option => option.Label).Append("Exit")));

            if (selectedServerLabel == "Exit")
            {
                return 0;
            }

            var selectedServer = serverOptions.First(option => option.Label == selectedServerLabel).Server;

            await ShowServerAsync(session, selectedServer);
        }
    }

    private static async Task ShowServerAsync(TuiSession session, ServerInspection server)
    {
        var console = session.Console;

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

        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);

            var bookmarksForServer = session.Bookmarks.ForServer(server.Name);
            var bookmarksLabel = bookmarksForServer.Count == 0
                ? "Bookmarks"
                : $"Bookmarks ({bookmarksForServer.Count})";

            var choice = console.Prompt(
                new SelectionPrompt<string>()
                    .Title("Choose a section")
                    .AddChoices("Overview", "Tools", "Resources", "Resource Templates", "Prompts", bookmarksLabel, "Back"));

            if (choice == "Back")
            {
                return;
            }

            if (choice == bookmarksLabel)
            {
                await ShowBookmarksAsync(console, server, session.Bookmarks, session.WaitForKey);
                continue;
            }

            console.Clear();
            RenderServerSummary(console, server);
            switch (choice)
            {
                case "Overview":
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

    private static async Task ShowToolsAsync(TuiSession session, ServerInspection server, IDictionary<string, string> filters)
    {
        var console = session.Console;
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            var filter = filters["Tools"];
            RenderTools(console, server, filter);

            if (server.Tools.Error is not null)
            {
                console.MarkupLine("\n[grey]Press any key to continue...[/]");
                await session.WaitForKey();
                return;
            }

            var matches = FilterTools(server.Tools.Items, filter);
            var choices = BuildItemChoices(matches.Select(t => t.Name), filter);
            var action = console.Prompt(new SelectionPrompt<string>().UseConverter(Markup.Escape).Title("Tools").PageSize(15).AddChoices(choices));

            if (action == BackChoice) return;
            if (action == SearchChoice)
            {
                filters["Tools"] = console.Prompt(new TextPrompt<string>("Filter (substring, case-insensitive):").AllowEmpty());
                continue;
            }
            if (action == ClearFilterChoice)
            {
                filters["Tools"] = string.Empty;
                continue;
            }

            // Drilldown: selected tool name -> render schema preview + actions.
            var name = StripPrefix(action);
            var tool = matches.FirstOrDefault(t => t.Name == name);
            if (tool is null)
            {
                continue;
            }

            await ShowToolDetailAsync(session, server, tool);
        }
    }

    private static async Task ShowToolDetailAsync(TuiSession session, ServerInspection server, ToolInfo tool)
    {
        var console = session.Console;
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            RenderToolDetail(console, tool);

            var bookmark = new TuiBookmark(server.Name, TuiBookmarkKind.Tool, tool.Name);
            var toggle = session.Bookmarks.Contains(bookmark) ? "Unbookmark" : "Bookmark";

            var choices = new List<string>();
            if (session.Invoker is not null)
            {
                choices.Add(CallChoice);
            }
            choices.Add(toggle);
            choices.Add("Back");

            var action = console.Prompt(new SelectionPrompt<string>().Title("Tool actions").AddChoices(choices));
            if (action == "Back") return;
            if (action == CallChoice)
            {
                await InvokeToolAsync(session, server, tool);
                continue;
            }

            await ToggleBookmarkAsync(session, bookmark);
        }
    }

    private static async Task ShowResourcesAsync(TuiSession session, ServerInspection server, IDictionary<string, string> filters)
    {
        var console = session.Console;
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            var filter = filters["Resources"];
            RenderResources(console, server, filter);

            if (server.Resources.Error is not null)
            {
                console.MarkupLine("\n[grey]Press any key to continue...[/]");
                await session.WaitForKey();
                return;
            }

            var matches = FilterResources(server.Resources.Items, filter);
            var choices = BuildItemChoices(matches.Select(r => r.Name ?? r.Uri ?? "(unnamed)"), filter);
            var action = console.Prompt(new SelectionPrompt<string>().UseConverter(Markup.Escape).Title("Resources").PageSize(15).AddChoices(choices));

            if (action == BackChoice) return;
            if (action == SearchChoice)
            {
                filters["Resources"] = console.Prompt(new TextPrompt<string>("Filter (substring, case-insensitive):").AllowEmpty());
                continue;
            }
            if (action == ClearFilterChoice)
            {
                filters["Resources"] = string.Empty;
                continue;
            }

            var name = StripPrefix(action);
            var resource = matches.FirstOrDefault(r => (r.Name ?? r.Uri ?? "(unnamed)") == name);
            if (resource is null) continue;

            await ShowResourceDetailAsync(session, server, resource);
        }
    }

    private static async Task ShowResourceDetailAsync(TuiSession session, ServerInspection server, ResourceInfo resource)
    {
        var console = session.Console;
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            RenderResourceDetail(console, resource);

            var bookmark = new TuiBookmark(server.Name, TuiBookmarkKind.Resource, resource.Uri ?? resource.Name ?? "(unnamed)");
            var toggle = session.Bookmarks.Contains(bookmark) ? "Unbookmark" : "Bookmark";

            var choices = new List<string>();
            if (session.Invoker is not null && !string.IsNullOrEmpty(resource.Uri))
            {
                choices.Add(ReadChoice);
            }
            choices.Add(toggle);
            choices.Add("Back");

            var action = console.Prompt(new SelectionPrompt<string>().Title("Resource actions").AddChoices(choices));
            if (action == "Back") return;
            if (action == ReadChoice)
            {
                await InvokeReadAsync(session, server, resource.Uri!, arguments: null, label: $"read {resource.Uri}");
                continue;
            }

            await ToggleBookmarkAsync(session, bookmark);
        }
    }

    private static async Task ShowResourceTemplatesAsync(TuiSession session, ServerInspection server, IDictionary<string, string> filters)
    {
        var console = session.Console;
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            var filter = filters["Resource Templates"];
            RenderResourceTemplates(console, server, filter);

            if (server.ResourceTemplates.Error is not null)
            {
                console.MarkupLine("\n[grey]Press any key to continue...[/]");
                await session.WaitForKey();
                return;
            }

            var matches = FilterResourceTemplates(server.ResourceTemplates.Items, filter);
            var choices = BuildItemChoices(matches.Select(t => t.Name ?? t.UriTemplate ?? "(unnamed)"), filter);
            var action = console.Prompt(new SelectionPrompt<string>().UseConverter(Markup.Escape).Title("Resource Templates").PageSize(15).AddChoices(choices));

            if (action == BackChoice) return;
            if (action == SearchChoice)
            {
                filters["Resource Templates"] = console.Prompt(new TextPrompt<string>("Filter (substring, case-insensitive):").AllowEmpty());
                continue;
            }
            if (action == ClearFilterChoice)
            {
                filters["Resource Templates"] = string.Empty;
                continue;
            }

            var name = StripPrefix(action);
            var template = matches.FirstOrDefault(t => (t.Name ?? t.UriTemplate ?? "(unnamed)") == name);
            if (template is null) continue;

            await ShowResourceTemplateDetailAsync(session, server, template);
        }
    }

    private static async Task ShowResourceTemplateDetailAsync(TuiSession session, ServerInspection server, ResourceTemplateInfo template)
    {
        var console = session.Console;
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            RenderResourceTemplateDetail(console, template);

            var bookmark = new TuiBookmark(server.Name, TuiBookmarkKind.ResourceTemplate, template.UriTemplate ?? template.Name ?? "(unnamed)");
            var toggle = session.Bookmarks.Contains(bookmark) ? "Unbookmark" : "Bookmark";

            var choices = new List<string>();
            if (session.Invoker is not null && !string.IsNullOrEmpty(template.UriTemplate))
            {
                choices.Add(ReadChoice);
            }
            choices.Add(toggle);
            choices.Add("Back");

            var action = console.Prompt(new SelectionPrompt<string>().Title("Resource template actions").AddChoices(choices));
            if (action == "Back") return;
            if (action == ReadChoice)
            {
                var variables = ArgumentElicitor.ElicitTemplateVariables(console, template.UriTemplate!);
                await InvokeReadAsync(session, server, template.UriTemplate!, variables, label: $"read {template.UriTemplate}");
                continue;
            }

            await ToggleBookmarkAsync(session, bookmark);
        }
    }

    private static async Task ShowPromptsAsync(TuiSession session, ServerInspection server, IDictionary<string, string> filters)
    {
        var console = session.Console;
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            var filter = filters["Prompts"];
            RenderPrompts(console, server, filter);

            if (server.Prompts.Error is not null)
            {
                console.MarkupLine("\n[grey]Press any key to continue...[/]");
                await session.WaitForKey();
                return;
            }

            var matches = FilterPrompts(server.Prompts.Items, filter);
            var choices = BuildItemChoices(matches.Select(p => p.Name), filter);
            var action = console.Prompt(new SelectionPrompt<string>().UseConverter(Markup.Escape).Title("Prompts").PageSize(15).AddChoices(choices));

            if (action == BackChoice) return;
            if (action == SearchChoice)
            {
                filters["Prompts"] = console.Prompt(new TextPrompt<string>("Filter (substring, case-insensitive):").AllowEmpty());
                continue;
            }
            if (action == ClearFilterChoice)
            {
                filters["Prompts"] = string.Empty;
                continue;
            }

            var name = StripPrefix(action);
            var prompt = matches.FirstOrDefault(p => p.Name == name);
            if (prompt is null) continue;

            await ShowPromptDetailAsync(session, server, prompt);
        }
    }

    private static async Task ShowPromptDetailAsync(TuiSession session, ServerInspection server, PromptInfo prompt)
    {
        var console = session.Console;
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            RenderPromptDetail(console, prompt);

            var bookmark = new TuiBookmark(server.Name, TuiBookmarkKind.Prompt, prompt.Name);
            var toggle = session.Bookmarks.Contains(bookmark) ? "Unbookmark" : "Bookmark";

            var choices = new List<string>();
            if (session.Invoker is not null)
            {
                choices.Add(GetPromptChoice);
            }
            choices.Add(toggle);
            choices.Add("Back");

            var action = console.Prompt(new SelectionPrompt<string>().Title("Prompt actions").AddChoices(choices));
            if (action == "Back") return;
            if (action == GetPromptChoice)
            {
                await InvokePromptAsync(session, server, prompt);
                continue;
            }

            await ToggleBookmarkAsync(session, bookmark);
        }
    }

    // --- Invocation drivers -----------------------------------------

    private static async Task InvokeToolAsync(TuiSession session, ServerInspection server, ToolInfo tool)
    {
        var console = session.Console;
        var arguments = ArgumentElicitor.ElicitToolArguments(console, tool.InputSchema);

        if (!ConfirmRun(session, "call", tool.Name, arguments, server))
        {
            return;
        }

        var result = await SafeInvokeAsync(() => session.Invoker!.CallToolAsync(server.Name, tool.Name, arguments, CancellationToken.None));
        await RenderInvocationResultAsync(session, $"call {tool.Name}", result);
    }

    private static async Task InvokeReadAsync(TuiSession session, ServerInspection server, string resourceOrTemplate, JsonObject? arguments, string label)
    {
        if (!ConfirmRun(session, "read", resourceOrTemplate, arguments, server))
        {
            return;
        }

        var result = await SafeInvokeAsync(() => session.Invoker!.ReadResourceAsync(server.Name, resourceOrTemplate, arguments, CancellationToken.None));
        await RenderInvocationResultAsync(session, label, result);
    }

    private static async Task InvokePromptAsync(TuiSession session, ServerInspection server, PromptInfo prompt)
    {
        var console = session.Console;
        var arguments = ArgumentElicitor.ElicitPromptArguments(console, prompt.Arguments);

        if (!ConfirmRun(session, "prompt", prompt.Name, arguments, server))
        {
            return;
        }

        var result = await SafeInvokeAsync(() => session.Invoker!.GetPromptAsync(server.Name, prompt.Name, arguments, CancellationToken.None));
        await RenderInvocationResultAsync(session, $"prompt {prompt.Name}", result);
    }

    private static bool ConfirmRun(TuiSession session, string verb, string subject, JsonObject? arguments, ServerInspection server)
    {
        var console = session.Console;
        var equivalent = ArgumentElicitor.BuildEquivalentCommand(verb, subject, arguments, server.Transport, server.Target);
        console.MarkupLine($"[grey]equivalent:[/] {Markup.Escape(equivalent)}");
        return console.Confirm("Run now?");
    }

    private static async Task<InvokeResult> SafeInvokeAsync(Func<Task<InvokeResult>> invoke)
    {
        try
        {
            return await invoke();
        }
        catch (Exception ex)
        {
            return new InvokeResult($"{ex.GetType().Name}: {ex.Message}", HasErrors: true);
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
        console.MarkupLine("\n[grey]Press any key to continue...[/]");
        await session.WaitForKey();
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

    private const string SearchChoice = "[Search…]";
    private const string ClearFilterChoice = "[Clear filter]";
    private const string BackChoice = "[Back]";
    private const string CallChoice = "Call tool";
    private const string ReadChoice = "Read";
    private const string GetPromptChoice = "Get prompt";

    private static IEnumerable<string> BuildItemChoices(IEnumerable<string> names, string filter)
    {
        // Three control rows always show: Search, optional Clear-filter, Back.
        yield return SearchChoice;
        if (!string.IsNullOrEmpty(filter))
        {
            yield return ClearFilterChoice;
        }
        yield return BackChoice;

        foreach (var name in names)
        {
            yield return name;
        }
    }

    // Item choices are presented verbatim today; if we ever prefix them
    // (e.g. "[*] foo"), StripPrefix keeps the lookup logic in one place.
    private static string StripPrefix(string choice) => choice;

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
        var panel = new Panel($"[bold]{Markup.Escape(server.Name)}[/]\n[grey]{Markup.Escape(server.Target)}[/]")
        {
            Header = new PanelHeader($"{server.Transport} server")
        };
        console.Write(panel);
    }

    internal static void RenderOverview(IAnsiConsole console, ServerInspection server)
    {
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

    internal static void RenderTools(IAnsiConsole console, ServerInspection server)
        => RenderTools(console, server, filter: string.Empty);

    internal static void RenderTools(IAnsiConsole console, ServerInspection server, string filter)
    {
        if (server.Tools.Error is not null)
        {
            console.MarkupLine($"[red]{Markup.Escape(server.Tools.Error)}[/]");
            return;
        }

        var items = FilterTools(server.Tools.Items, filter);
        RenderFilterHeader(console, filter, items.Count, server.Tools.Items.Count);

        var table = new Table().RoundedBorder();
        table.AddColumn("Tool");
        table.AddColumn("Description");
        foreach (var item in items)
        {
            table.AddRow(Markup.Escape(item.Name), Markup.Escape(item.Description ?? string.Empty));
        }

        console.Write(table);
    }

    internal static void RenderResources(IAnsiConsole console, ServerInspection server)
        => RenderResources(console, server, filter: string.Empty);

    internal static void RenderResources(IAnsiConsole console, ServerInspection server, string filter)
    {
        if (server.Resources.Error is not null)
        {
            console.MarkupLine($"[red]{Markup.Escape(server.Resources.Error)}[/]");
            return;
        }

        var items = FilterResources(server.Resources.Items, filter);
        RenderFilterHeader(console, filter, items.Count, server.Resources.Items.Count);

        var table = new Table().RoundedBorder();
        table.AddColumn("Name");
        table.AddColumn("Uri");
        table.AddColumn("Mime");
        foreach (var item in items)
        {
            table.AddRow(Markup.Escape(item.Name ?? string.Empty), Markup.Escape(item.Uri ?? string.Empty), Markup.Escape(item.MimeType ?? string.Empty));
        }

        console.Write(table);
    }

    internal static void RenderResourceTemplates(IAnsiConsole console, ServerInspection server)
        => RenderResourceTemplates(console, server, filter: string.Empty);

    internal static void RenderResourceTemplates(IAnsiConsole console, ServerInspection server, string filter)
    {
        if (server.ResourceTemplates.Error is not null)
        {
            console.MarkupLine($"[red]{Markup.Escape(server.ResourceTemplates.Error)}[/]");
            return;
        }

        var items = FilterResourceTemplates(server.ResourceTemplates.Items, filter);
        RenderFilterHeader(console, filter, items.Count, server.ResourceTemplates.Items.Count);

        var table = new Table().RoundedBorder();
        table.AddColumn("Name");
        table.AddColumn("Template");
        table.AddColumn("Mime");
        foreach (var item in items)
        {
            table.AddRow(Markup.Escape(item.Name ?? string.Empty), Markup.Escape(item.UriTemplate ?? string.Empty), Markup.Escape(item.MimeType ?? string.Empty));
        }

        console.Write(table);
    }

    internal static void RenderPrompts(IAnsiConsole console, ServerInspection server)
        => RenderPrompts(console, server, filter: string.Empty);

    internal static void RenderPrompts(IAnsiConsole console, ServerInspection server, string filter)
    {
        if (server.Prompts.Error is not null)
        {
            console.MarkupLine($"[red]{Markup.Escape(server.Prompts.Error)}[/]");
            return;
        }

        var items = FilterPrompts(server.Prompts.Items, filter);
        RenderFilterHeader(console, filter, items.Count, server.Prompts.Items.Count);

        var table = new Table().RoundedBorder();
        table.AddColumn("Prompt");
        table.AddColumn("Arguments");
        foreach (var item in items)
        {
            var arguments = string.Join(", ", item.Arguments.Select(argument => argument.Required ? $"{argument.Name}*" : argument.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
            table.AddRow(Markup.Escape(item.Name), Markup.Escape(arguments));
        }

        console.Write(table);
    }

    /// <summary>
    /// Renders a tool's name + description + a tree preview of its <c>inputSchema</c>.
    /// The tree summarises top-level properties (name, type, required, description). It is
    /// not a JSON Schema validator - the goal is fast visual orientation, not exhaustive
    /// schema rendering.
    /// </summary>
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

    private static void RenderFilterHeader(IAnsiConsole console, string filter, int shown, int total)
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
    /// "press a key" hook (swappable in tests), the bookmark store, and the optional invoker
    /// that turns the explorer into an interactive caller. <see cref="Invoker"/> is null for
    /// pure-render callers (library hosts, unit tests) - in that case the Call/Read/Get actions
    /// are simply not offered.
    /// </summary>
    private sealed record TuiSession(
        IAnsiConsole Console,
        Func<Task> WaitForKey,
        TuiBookmarkStore Bookmarks,
        IMcpInvoker? Invoker);
}
