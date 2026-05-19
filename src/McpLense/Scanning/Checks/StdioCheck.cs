using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpLense.Scanning.Checks;

/// <summary>
/// Surfaces stdio target configuration verbatim: command, args, cwd, env. Skipped for HTTP
/// targets.
/// </summary>
internal sealed class StdioCheck : IScanCheck
{
    public string Id => "stdio";
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();
    public bool IsEnabledByDefault => true;

    public Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (context.Server.Kind != ConnectionKind.Stdio)
        {
            return Task.FromResult(CheckOutcome.Skipped);
        }

        var data = new StdioData(
            Command: context.Server.Command ?? string.Empty,
            Arguments: context.Server.CommandArguments.ToArray(),
            WorkingDirectory: context.Server.WorkingDirectory,
            Environment: new Dictionary<string, string>(context.Server.Environment, StringComparer.OrdinalIgnoreCase));

        return Task.FromResult(new CheckOutcome(Ran: true, Data: JsonSerializer.SerializeToNode(data, AuthCheck.SerializerOptions), Error: null));
    }

    internal sealed record StdioData(
        string Command,
        IReadOnlyList<string> Arguments,
        string? WorkingDirectory,
        IReadOnlyDictionary<string, string> Environment);
}
