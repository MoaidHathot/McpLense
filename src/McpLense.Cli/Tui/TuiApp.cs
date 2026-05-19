using Spectre.Console;

namespace McpLense;

internal static class TuiApp
{
    public static Task<int> RunAsync(ParsedCommand command)
        => RunAsync(command, console: null, waitForKey: null);

    internal static async Task<int> RunAsync(
        ParsedCommand command,
        IAnsiConsole? console,
        Func<Task>? waitForKey)
    {
        console ??= AnsiConsole.Console;
        waitForKey ??= DefaultWaitForKey;

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

        return await RenderAsync(report, console, waitForKey);
    }

    internal static async Task<int> RenderAsync(
        InspectReport report,
        IAnsiConsole console,
        Func<Task> waitForKey)
    {
        var servers = report.Servers;
        if (servers.Count == 0)
        {
            console.MarkupLine("[red]No servers were resolved.[/]");
            return 1;
        }

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

            await ShowServerAsync(console, selectedServer, waitForKey);
        }
    }

    private static async Task ShowServerAsync(IAnsiConsole console, ServerInspection server, Func<Task> waitForKey)
    {
        while (true)
        {
            console.Clear();
            RenderServerSummary(console, server);

            var choice = console.Prompt(
                new SelectionPrompt<string>()
                    .Title("Choose a section")
                    .AddChoices("Overview", "Tools", "Resources", "Resource Templates", "Prompts", "Back"));

            if (choice == "Back")
            {
                return;
            }

            console.Clear();
            RenderServerSummary(console, server);
            switch (choice)
            {
                case "Overview":
                    RenderOverview(console, server);
                    break;
                case "Tools":
                    RenderTools(console, server);
                    break;
                case "Resources":
                    RenderResources(console, server);
                    break;
                case "Resource Templates":
                    RenderResourceTemplates(console, server);
                    break;
                case "Prompts":
                    RenderPrompts(console, server);
                    break;
            }

            console.MarkupLine("\n[grey]Press any key to continue...[/]");
            await waitForKey();
        }
    }

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
    {
        if (server.Tools.Error is not null)
        {
            console.MarkupLine($"[red]{Markup.Escape(server.Tools.Error)}[/]");
            return;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("Tool");
        table.AddColumn("Description");
        foreach (var item in server.Tools.Items)
        {
            table.AddRow(Markup.Escape(item.Name), Markup.Escape(item.Description ?? string.Empty));
        }

        console.Write(table);
    }

    internal static void RenderResources(IAnsiConsole console, ServerInspection server)
    {
        if (server.Resources.Error is not null)
        {
            console.MarkupLine($"[red]{Markup.Escape(server.Resources.Error)}[/]");
            return;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("Name");
        table.AddColumn("Uri");
        table.AddColumn("Mime");
        foreach (var item in server.Resources.Items)
        {
            table.AddRow(Markup.Escape(item.Name ?? string.Empty), Markup.Escape(item.Uri ?? string.Empty), Markup.Escape(item.MimeType ?? string.Empty));
        }

        console.Write(table);
    }

    internal static void RenderResourceTemplates(IAnsiConsole console, ServerInspection server)
    {
        if (server.ResourceTemplates.Error is not null)
        {
            console.MarkupLine($"[red]{Markup.Escape(server.ResourceTemplates.Error)}[/]");
            return;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("Name");
        table.AddColumn("Template");
        table.AddColumn("Mime");
        foreach (var item in server.ResourceTemplates.Items)
        {
            table.AddRow(Markup.Escape(item.Name ?? string.Empty), Markup.Escape(item.UriTemplate ?? string.Empty), Markup.Escape(item.MimeType ?? string.Empty));
        }

        console.Write(table);
    }

    internal static void RenderPrompts(IAnsiConsole console, ServerInspection server)
    {
        if (server.Prompts.Error is not null)
        {
            console.MarkupLine($"[red]{Markup.Escape(server.Prompts.Error)}[/]");
            return;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("Prompt");
        table.AddColumn("Arguments");
        foreach (var item in server.Prompts.Items)
        {
            var arguments = string.Join(", ", item.Arguments.Select(argument => argument.Required ? $"{argument.Name}*" : argument.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
            table.AddRow(Markup.Escape(item.Name), Markup.Escape(arguments));
        }

        console.Write(table);
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
