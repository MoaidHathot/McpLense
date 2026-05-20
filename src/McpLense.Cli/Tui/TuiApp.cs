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

        return await RenderAsync(report, console, waitForKey, bookmarkStore);
    }

    internal static Task<int> RenderAsync(
        InspectReport report,
        IAnsiConsole console,
        Func<Task> waitForKey)
        => RenderAsync(report, console, waitForKey, bookmarkStore: null);

    internal static async Task<int> RenderAsync(
        InspectReport report,
        IAnsiConsole console,
        Func<Task> waitForKey,
        TuiBookmarkStore? bookmarkStore)
    {
        var servers = report.Servers;
        if (servers.Count == 0)
        {
            console.MarkupLine("[red]No servers were resolved.[/]");
            return 1;
        }

        bookmarkStore ??= TuiBookmarkStore.InMemory();

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

            await ShowServerAsync(console, selectedServer, waitForKey, bookmarkStore);
        }
    }

    private static async Task ShowServerAsync(
        IAnsiConsole console,
        ServerInspection server,
        Func<Task> waitForKey,
        TuiBookmarkStore bookmarks)
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

        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);

            var bookmarksForServer = bookmarks.ForServer(server.Name);
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
                await ShowBookmarksAsync(console, server, bookmarks, waitForKey);
                continue;
            }

            console.Clear();
            RenderServerSummary(console, server);
            switch (choice)
            {
                case "Overview":
                    RenderOverview(console, server);
                    console.MarkupLine("\n[grey]Press any key to continue...[/]");
                    await waitForKey();
                    break;
                case "Tools":
                    await ShowToolsAsync(console, server, filters, bookmarks, waitForKey);
                    break;
                case "Resources":
                    await ShowResourcesAsync(console, server, filters, bookmarks, waitForKey);
                    break;
                case "Resource Templates":
                    await ShowResourceTemplatesAsync(console, server, filters, bookmarks, waitForKey);
                    break;
                case "Prompts":
                    await ShowPromptsAsync(console, server, filters, bookmarks, waitForKey);
                    break;
            }
        }
    }

    // --- Sections with search + bookmarks + drilldown ---------------

    private static async Task ShowToolsAsync(
        IAnsiConsole console,
        ServerInspection server,
        IDictionary<string, string> filters,
        TuiBookmarkStore bookmarks,
        Func<Task> waitForKey)
    {
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            var filter = filters["Tools"];
            RenderTools(console, server, filter);

            if (server.Tools.Error is not null)
            {
                console.MarkupLine("\n[grey]Press any key to continue...[/]");
                await waitForKey();
                return;
            }

            var matches = FilterTools(server.Tools.Items, filter);
            var choices = BuildItemChoices(matches.Select(t => t.Name), filter);
            var action = console.Prompt(new SelectionPrompt<string>().Title("Tools").PageSize(15).AddChoices(choices));

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

            // Drilldown: selected tool name -> render schema preview + bookmark toggle.
            var name = StripPrefix(action);
            var tool = matches.FirstOrDefault(t => t.Name == name);
            if (tool is null)
            {
                continue;
            }

            await ShowToolDetailAsync(console, server, tool, bookmarks, waitForKey);
        }
    }

    private static async Task ShowToolDetailAsync(
        IAnsiConsole console,
        ServerInspection server,
        ToolInfo tool,
        TuiBookmarkStore bookmarks,
        Func<Task> waitForKey)
    {
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            RenderToolDetail(console, tool);

            var bookmark = new TuiBookmark(server.Name, TuiBookmarkKind.Tool, tool.Name);
            var bookmarked = bookmarks.Contains(bookmark);
            var toggle = bookmarked ? "Unbookmark" : "Bookmark";

            var action = console.Prompt(new SelectionPrompt<string>().Title("Tool actions").AddChoices(toggle, "Back"));
            if (action == "Back") return;
            if (action == toggle)
            {
                bookmarks.Toggle(bookmark);
                console.MarkupLine(bookmarked ? "[grey]Removed bookmark.[/]" : "[green]Bookmarked.[/]");
                console.MarkupLine("[grey]Press any key to continue...[/]");
                await waitForKey();
            }
        }
    }

    private static async Task ShowResourcesAsync(
        IAnsiConsole console,
        ServerInspection server,
        IDictionary<string, string> filters,
        TuiBookmarkStore bookmarks,
        Func<Task> waitForKey)
    {
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            var filter = filters["Resources"];
            RenderResources(console, server, filter);

            if (server.Resources.Error is not null)
            {
                console.MarkupLine("\n[grey]Press any key to continue...[/]");
                await waitForKey();
                return;
            }

            var matches = FilterResources(server.Resources.Items, filter);
            var choices = BuildItemChoices(matches.Select(r => r.Name ?? r.Uri ?? "(unnamed)"), filter);
            var action = console.Prompt(new SelectionPrompt<string>().Title("Resources").PageSize(15).AddChoices(choices));

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

            var bookmark = new TuiBookmark(server.Name, TuiBookmarkKind.Resource, resource.Uri ?? resource.Name ?? "(unnamed)");
            await ToggleBookmarkInteractiveAsync(console, bookmarks, bookmark, waitForKey);
        }
    }

    private static async Task ShowResourceTemplatesAsync(
        IAnsiConsole console,
        ServerInspection server,
        IDictionary<string, string> filters,
        TuiBookmarkStore bookmarks,
        Func<Task> waitForKey)
    {
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            var filter = filters["Resource Templates"];
            RenderResourceTemplates(console, server, filter);

            if (server.ResourceTemplates.Error is not null)
            {
                console.MarkupLine("\n[grey]Press any key to continue...[/]");
                await waitForKey();
                return;
            }

            var matches = FilterResourceTemplates(server.ResourceTemplates.Items, filter);
            var choices = BuildItemChoices(matches.Select(t => t.Name ?? t.UriTemplate ?? "(unnamed)"), filter);
            var action = console.Prompt(new SelectionPrompt<string>().Title("Resource Templates").PageSize(15).AddChoices(choices));

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

            var bookmark = new TuiBookmark(server.Name, TuiBookmarkKind.ResourceTemplate, template.UriTemplate ?? template.Name ?? "(unnamed)");
            await ToggleBookmarkInteractiveAsync(console, bookmarks, bookmark, waitForKey);
        }
    }

    private static async Task ShowPromptsAsync(
        IAnsiConsole console,
        ServerInspection server,
        IDictionary<string, string> filters,
        TuiBookmarkStore bookmarks,
        Func<Task> waitForKey)
    {
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);
            var filter = filters["Prompts"];
            RenderPrompts(console, server, filter);

            if (server.Prompts.Error is not null)
            {
                console.MarkupLine("\n[grey]Press any key to continue...[/]");
                await waitForKey();
                return;
            }

            var matches = FilterPrompts(server.Prompts.Items, filter);
            var choices = BuildItemChoices(matches.Select(p => p.Name), filter);
            var action = console.Prompt(new SelectionPrompt<string>().Title("Prompts").PageSize(15).AddChoices(choices));

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

            var bookmark = new TuiBookmark(server.Name, TuiBookmarkKind.Prompt, prompt.Name);
            await ToggleBookmarkInteractiveAsync(console, bookmarks, bookmark, waitForKey);
        }
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

    private static async Task ToggleBookmarkInteractiveAsync(
        IAnsiConsole console,
        TuiBookmarkStore bookmarks,
        TuiBookmark bookmark,
        Func<Task> waitForKey)
    {
        var existed = bookmarks.Contains(bookmark);
        var prompt = existed ? "Unbookmark" : "Bookmark";
        var action = console.Prompt(new SelectionPrompt<string>().Title("Selected").AddChoices(prompt, "Back"));
        if (action == prompt)
        {
            bookmarks.Toggle(bookmark);
            console.MarkupLine(existed ? "[grey]Removed bookmark.[/]" : "[green]Bookmarked.[/]");
            console.MarkupLine("[grey]Press any key to continue...[/]");
            await waitForKey();
        }
    }

    // --- Choice helpers --------------------------------------------------

    private const string SearchChoice = "[Search…]";
    private const string ClearFilterChoice = "[Clear filter]";
    private const string BackChoice = "[Back]";

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
}
