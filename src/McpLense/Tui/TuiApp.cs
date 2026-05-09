using Spectre.Console;

namespace McpLense;

internal static class TuiApp
{
    public static async Task<int> RunAsync(ParsedCommand command)
    {
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

        var servers = report.Servers;
        if (servers.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No servers were resolved.[/]");
            return 1;
        }

        while (true)
        {
            AnsiConsole.Clear();
            var serverOptions = servers
                .Select((server, index) => new { Label = $"{index + 1}. {server.Name} [{server.Transport}] {server.Target}", Server = server })
                .ToArray();

            var selectedServerLabel = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select an MCP server")
                    .PageSize(10)
                    .AddChoices(serverOptions.Select(option => option.Label).Append("Exit")));

            if (selectedServerLabel == "Exit")
            {
                return 0;
            }

            var selectedServer = serverOptions.First(option => option.Label == selectedServerLabel).Server;

            await ShowServerAsync(selectedServer);
        }
    }

    private static Task ShowServerAsync(ServerInspection server)
    {
        while (true)
        {
            AnsiConsole.Clear();
            RenderServerSummary(server);

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Choose a section")
                    .AddChoices("Overview", "Tools", "Resources", "Resource Templates", "Prompts", "Back"));

            if (choice == "Back")
            {
                return Task.CompletedTask;
            }

            AnsiConsole.Clear();
            RenderServerSummary(server);
            switch (choice)
            {
                case "Overview":
                    RenderOverview(server);
                    break;
                case "Tools":
                    RenderTools(server);
                    break;
                case "Resources":
                    RenderResources(server);
                    break;
                case "Resource Templates":
                    RenderResourceTemplates(server);
                    break;
                case "Prompts":
                    RenderPrompts(server);
                    break;
            }

            AnsiConsole.MarkupLine("\n[grey]Press any key to continue...[/]");
            Console.ReadKey(true);
        }
    }

    private static void RenderServerSummary(ServerInspection server)
    {
        var panel = new Panel($"[bold]{Markup.Escape(server.Name)}[/]\n[grey]{Markup.Escape(server.Target)}[/]")
        {
            Header = new PanelHeader($"{server.Transport} server")
        };
        AnsiConsole.Write(panel);
    }

    private static void RenderOverview(ServerInspection server)
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
        AnsiConsole.Write(table);
    }

    private static void RenderTools(ServerInspection server)
    {
        if (server.Tools.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(server.Tools.Error)}[/]");
            return;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("Tool");
        table.AddColumn("Description");
        foreach (var item in server.Tools.Items)
        {
            table.AddRow(Markup.Escape(item.Name), Markup.Escape(item.Description ?? string.Empty));
        }

        AnsiConsole.Write(table);
    }

    private static void RenderResources(ServerInspection server)
    {
        if (server.Resources.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(server.Resources.Error)}[/]");
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

        AnsiConsole.Write(table);
    }

    private static void RenderResourceTemplates(ServerInspection server)
    {
        if (server.ResourceTemplates.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(server.ResourceTemplates.Error)}[/]");
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

        AnsiConsole.Write(table);
    }

    private static void RenderPrompts(ServerInspection server)
    {
        if (server.Prompts.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(server.Prompts.Error)}[/]");
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

        AnsiConsole.Write(table);
    }

    private static string SectionStatus<T>(SectionResult<T> section)
        => section.Error is not null ? $"error: {section.Error}" : section.Supported ? "ok" : "not supported";

    private static string FormatCapabilities(CapabilitySnapshot capabilities)
    {
        var names = new List<string>();
        if (capabilities.Tools) names.Add("tools");
        if (capabilities.Resources) names.Add("resources");
        if (capabilities.Prompts) names.Add("prompts");
        if (capabilities.Logging) names.Add("logging");
        if (capabilities.Completions) names.Add("completions");
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }
}
