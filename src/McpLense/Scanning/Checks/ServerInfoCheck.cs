using System.Text.Json.Nodes;

namespace McpLense.Scanning.Checks;

/// <summary>
/// Server identity from the MCP <c>initialize</c> response (the <c>Implementation</c>
/// block): name, title, version, description, websiteUrl, icons, _meta. Verbatim, no
/// labelling.
/// </summary>
internal sealed class ServerInfoCheck : IScanCheck
{
    public string Id => "serverInfo";
    public IReadOnlyList<string> DependsOn => new[] { "auth" };
    public bool IsEnabledByDefault => true;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var client = await CheckSessionHelpers.TryGetSessionAsync(context, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new CheckOutcome(Ran: true, Data: null, Error: context.SessionError ?? "No MCP session available.");
        }

        var serverInfo = client.ServerInfo;
        if (serverInfo is null)
        {
            return new CheckOutcome(Ran: true, Data: null, Error: "Server did not advertise an Implementation block.");
        }

        var data = new ServerInfoData(
            Name: serverInfo.Name,
            Title: serverInfo.Title,
            Version: serverInfo.Version,
            Description: serverInfo.Description,
            WebsiteUrl: serverInfo.WebsiteUrl,
            Icons: CheckSessionHelpers.SafeNode(serverInfo.Icons),
            // Implementation doesn't expose Meta in the current SDK; keep reflection here
            // until the SDK formalises it (older _meta blocks still surface via the protocol
            // payload's tail dictionary).
            Meta: CheckSessionHelpers.SafeNode(CheckSessionHelpers.GetProp(serverInfo, "Meta")));

        return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(data), Error: null);
    }

    internal sealed record ServerInfoData(
        string? Name,
        string? Title,
        string? Version,
        string? Description,
        string? WebsiteUrl,
        JsonNode? Icons,
        JsonNode? Meta);
}
