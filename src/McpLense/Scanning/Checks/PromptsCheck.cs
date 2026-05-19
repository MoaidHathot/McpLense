using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace McpLense.Scanning.Checks;

internal sealed class PromptsCheck : IScanCheck
{
    public string Id => "prompts";
    public IReadOnlyList<string> DependsOn => new[] { "auth" };
    public bool IsEnabledByDefault => true;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var client = await CheckSessionHelpers.TryGetSessionAsync(context, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new CheckOutcome(Ran: true, Data: null, Error: context.SessionError ?? "No MCP session available.");
        }

        if (client.ServerCapabilities?.Prompts is null)
        {
            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new PromptsData(true, context.ActiveFetchedVia, null, [])), Error: null);
        }

        IList<McpClientPrompt> prompts;
        try
        {
            prompts = await client.ListPromptsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new PromptsData(false, context.ActiveFetchedVia, $"{ex.GetType().Name}: {ex.Message}", [])), Error: null);
        }

        var items = prompts.Select(MapPrompt).ToArray();
        return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new PromptsData(true, context.ActiveFetchedVia, null, items)), Error: null);
    }

    private static PromptEntryExtended MapPrompt(McpClientPrompt prompt)
    {
        var protocolPrompt = prompt.ProtocolPrompt;
        var arguments = (protocolPrompt?.Arguments ?? [])
            .Select(arg => new PromptArgumentInfo(arg.Name, arg.Description, arg.Required ?? false))
            .ToArray();

        return new PromptEntryExtended(
            Name: prompt.Name,
            Title: protocolPrompt?.Title,
            Description: prompt.Description,
            Arguments: arguments,
            Icons: CheckSessionHelpers.SafeNode(CheckSessionHelpers.GetProp(protocolPrompt, "Icons")),
            Meta: CheckSessionHelpers.SafeNode(CheckSessionHelpers.GetProp(protocolPrompt, "Meta")));
    }

    internal sealed record PromptsData(
        bool Fetched,
        string? FetchedVia,
        string? FetchError,
        IReadOnlyList<PromptEntryExtended> Items);

    internal sealed record PromptEntryExtended(
        string Name,
        string? Title,
        string? Description,
        IReadOnlyList<PromptArgumentInfo> Arguments,
        JsonNode? Icons,
        JsonNode? Meta);
}
