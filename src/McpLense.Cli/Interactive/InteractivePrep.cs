using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace McpLense;

/// <summary>
/// Powers the <c>--interactive</c> / <c>-i</c> flag on the non-TUI <c>call</c> / <c>read</c> /
/// <c>prompt</c> commands. Inspects the target to discover the relevant schema (a tool's
/// <c>inputSchema</c>, a prompt's declared arguments, or a URI-template's variables), prompts the
/// user for each value via <see cref="ArgumentElicitor"/>, and returns a command with the
/// collected <see cref="ParsedCommand.Arguments"/> ready to execute.
/// </summary>
internal static class InteractivePrep
{
    public static async Task<ParsedCommand> FillAsync(
        ParsedCommand command,
        IAnsiConsole console,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        switch (command.Command)
        {
            case AppCommand.Call:
            {
                var schema = await FindToolSchemaAsync(command, jsonOptions, cancellationToken).ConfigureAwait(false);
                console.MarkupLine($"[grey]Interactive arguments for tool[/] [green]{Markup.Escape(command.Subject ?? string.Empty)}[/]:");
                return command with { Arguments = ArgumentElicitor.ElicitToolArguments(console, schema) };
            }

            case AppCommand.Prompt:
            {
                var arguments = await FindPromptArgumentsAsync(command, jsonOptions, cancellationToken).ConfigureAwait(false);
                console.MarkupLine($"[grey]Interactive arguments for prompt[/] [green]{Markup.Escape(command.Subject ?? string.Empty)}[/]:");
                return command with { Arguments = ArgumentElicitor.ElicitPromptArguments(console, arguments) };
            }

            case AppCommand.Read:
            {
                // A concrete URI needs nothing; a template's {variables} are elicited from the
                // subject string directly (no inspect round-trip required).
                var variables = ArgumentElicitor.ExtractTemplateVariables(command.Subject ?? string.Empty);
                if (variables.Count == 0)
                {
                    return command;
                }

                return command with { Arguments = ArgumentElicitor.ElicitTemplateVariables(console, command.Subject!) };
            }

            default:
                return command;
        }
    }

    private static async Task<JsonNode?> FindToolSchemaAsync(ParsedCommand command, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        var report = await InspectAsync(command, jsonOptions, cancellationToken).ConfigureAwait(false);
        foreach (var server in report.Servers)
        {
            if (!server.Tools.Supported)
            {
                continue;
            }

            foreach (var tool in server.Tools.Items)
            {
                if (string.Equals(tool.Name, command.Subject, StringComparison.Ordinal))
                {
                    return tool.InputSchema;
                }
            }
        }

        throw new UserInputException($"Tool '{command.Subject}' was not found on the target (cannot prompt for its arguments).");
    }

    private static async Task<IReadOnlyList<PromptArgumentInfo>> FindPromptArgumentsAsync(ParsedCommand command, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        var report = await InspectAsync(command, jsonOptions, cancellationToken).ConfigureAwait(false);
        foreach (var server in report.Servers)
        {
            if (!server.Prompts.Supported)
            {
                continue;
            }

            foreach (var prompt in server.Prompts.Items)
            {
                if (string.Equals(prompt.Name, command.Subject, StringComparison.Ordinal))
                {
                    return prompt.Arguments;
                }
            }
        }

        throw new UserInputException($"Prompt '{command.Subject}' was not found on the target (cannot prompt for its arguments).");
    }

    private static async Task<InspectReport> InspectAsync(ParsedCommand command, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        var inspectCommand = command with
        {
            Command = AppCommand.Inspect,
            ProgressEnabled = false,
            Interactive = false
        };

        var outcome = await McpExecutor.ExecuteAsync(inspectCommand, jsonOptions, cancellationToken).ConfigureAwait(false);
        return outcome.Payload as InspectReport
            ?? throw new InvalidOperationException("Interactive prep expected an inspect report.");
    }
}
