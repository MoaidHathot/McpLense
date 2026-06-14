using System.Text.Json.Nodes;
using Spectre.Console;

namespace McpLense;

/// <summary>
/// Powers the <c>--interactive</c> / <c>-i</c> flag on <c>call</c> / <c>read</c> / <c>prompt</c>.
/// Opens a single <see cref="IMcpSession"/> and uses it for everything: discovering the relevant
/// schema (a tool's input schema / a prompt's arguments), eliciting each value (with server-driven
/// completions where the server offers them), and running the invocation over the same connection.
/// </summary>
internal static class InteractiveRunner
{
    public static async Task<ExecutionOutcome> RunAsync(ParsedCommand command, IAnsiConsole console, CancellationToken cancellationToken)
    {
        await using var session = await McpExecutor.ConnectAsync(
            command with { ProgressEnabled = false, Interactive = false },
            cancellationToken).ConfigureAwait(false);

        object report = command.Command switch
        {
            AppCommand.Call => await RunCallAsync(session, command, console, cancellationToken).ConfigureAwait(false),
            AppCommand.Read => await RunReadAsync(session, command, console, cancellationToken).ConfigureAwait(false),
            AppCommand.Prompt => await RunPromptAsync(session, command, console, cancellationToken).ConfigureAwait(false),
            _ => throw new UserInputException($"--interactive is not supported for '{command.Command}'.")
        };

        return new ExecutionOutcome(report, InvocationRenderer.HasErrors(report));
    }

    private static async Task<object> RunCallAsync(IMcpSession session, ParsedCommand command, IAnsiConsole console, CancellationToken cancellationToken)
    {
        var tools = await session.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, command.Subject, StringComparison.Ordinal))
            ?? throw new UserInputException($"Tool '{command.Subject}' was not found on the target.");

        console.MarkupLine($"[grey]Interactive arguments for tool[/] [green]{Markup.Escape(tool.Name)}[/]:");
        var arguments = ArgumentElicitor.ElicitToolArguments(console, tool.InputSchema);
        return await session.CallToolAsync(tool.Name, arguments, progress: null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> RunPromptAsync(IMcpSession session, ParsedCommand command, IAnsiConsole console, CancellationToken cancellationToken)
    {
        var prompts = await session.ListPromptsAsync(cancellationToken).ConfigureAwait(false);
        var prompt = prompts.FirstOrDefault(p => string.Equals(p.Name, command.Subject, StringComparison.Ordinal))
            ?? throw new UserInputException($"Prompt '{command.Subject}' was not found on the target.");

        console.MarkupLine($"[grey]Interactive arguments for prompt[/] [green]{Markup.Escape(prompt.Name)}[/]:");
        var arguments = await ArgumentElicitor.ElicitPromptArgumentsAsync(
            console, prompt.Arguments, new PromptCompletionSource(session, prompt.Name)).ConfigureAwait(false);
        return await session.GetPromptAsync(prompt.Name, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> RunReadAsync(IMcpSession session, ParsedCommand command, IAnsiConsole console, CancellationToken cancellationToken)
    {
        var subject = command.Subject ?? throw new UserInputException("read requires a resource URI or template.");
        var variables = ArgumentElicitor.ExtractTemplateVariables(subject);

        JsonObject? arguments = null;
        if (variables.Count > 0)
        {
            console.MarkupLine($"[grey]Interactive variables for[/] [green]{Markup.Escape(subject)}[/]:");
            var filled = await ArgumentElicitor.ElicitTemplateVariablesAsync(
                console, subject, new TemplateCompletionSource(session, subject)).ConfigureAwait(false);
            arguments = filled.Count > 0 ? filled : null;
        }

        return await session.ReadResourceAsync(subject, arguments, cancellationToken).ConfigureAwait(false);
    }
}
