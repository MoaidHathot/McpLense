using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpLense.TestServer.Shared;

[McpServerToolType]
public sealed class EchoTools
{
    [McpServerTool(Name = "Echo"), Description("Echoes the provided message back to the caller.")]
    public static string Echo([Description("The message to echo")] string message)
        => $"echo: {message}";
}

[McpServerToolType]
public sealed class MathTools
{
    [McpServerTool(Name = "Add"), Description("Adds two integers.")]
    public static int Add(
        [Description("First addend")] int a,
        [Description("Second addend")] int b)
        => a + b;

    [McpServerTool(Name = "Divide"), Description("Divides two doubles. Throws when the divisor is zero.")]
    public static double Divide(
        [Description("Dividend")] double a,
        [Description("Divisor")] double b)
    {
        if (b == 0)
        {
            throw new ArgumentException("Cannot divide by zero.");
        }

        return a / b;
    }
}

[McpServerToolType]
public sealed class LongRunningTools
{
    [McpServerTool(Name = "RunWithProgress"), Description("Runs a few short steps and reports progress for each.")]
    public static async Task<string> RunWithProgress(
        [Description("Number of progress steps to emit")] int steps,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(steps, 1, 10);

        for (var index = 0; index < clamped; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress.Report(new ProgressNotificationValue
            {
                Progress = index + 1,
                Total = clamped,
                Message = $"step {index + 1}/{clamped}"
            });

            await Task.Delay(10, cancellationToken);
        }

        return $"completed {clamped} step(s)";
    }
}

[McpServerToolType]
public sealed class FailingTools
{
    [McpServerTool(Name = "Boom"), Description("Always throws to exercise error reporting.")]
    public static string Boom() => throw new InvalidOperationException("intentional failure");
}

[McpServerResourceType]
public sealed class TestResources
{
    [McpServerResource(UriTemplate = "config://app/settings", Name = "AppSettings", MimeType = "application/json")]
    [Description("Static application settings document.")]
    public static string GetSettings()
        => JsonSerializer.Serialize(new { theme = "dark", language = "en" });

    [McpServerResource(UriTemplate = "docs://articles/{id}", Name = "Article", MimeType = "text/markdown")]
    [Description("Returns a synthetic article keyed by id.")]
    public static TextResourceContents GetArticle(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new McpException("Article id is required.");
        }

        return new TextResourceContents
        {
            Uri = $"docs://articles/{id}",
            MimeType = "text/markdown",
            Text = $"# Article {id}\n\nSynthetic content for {id}."
        };
    }
}

[McpServerPromptType]
public sealed class TestPrompts
{
    [McpServerPrompt(Name = "Greet"), Description("Greets the named user.")]
    public static PromptMessage Greet(
        [Description("Name to greet")] string name)
        => new()
        {
            Role = Role.User,
            Content = new TextContentBlock { Text = $"Hello, {name}!" }
        };

    [McpServerPrompt(Name = "CodeReview"), Description("Builds a code review prompt.")]
    public static IEnumerable<PromptMessage> CodeReview(
        [Description("Programming language")] string language,
        [Description("Code to review")] string code) =>
        [
            new PromptMessage
            {
                Role = Role.User,
                Content = new TextContentBlock { Text = $"Please review this {language} snippet:\n\n```{language}\n{code}\n```" }
            }
        ];
}
