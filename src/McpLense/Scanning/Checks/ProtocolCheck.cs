using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace McpLense.Scanning.Checks;

/// <summary>
/// Protocol-level details from <c>initialize</c>: negotiated version, full capabilities
/// block (including experimental + extensions), verbatim server instructions, top-level
/// _meta. Adds <c>negotiatedTransport</c> / <c>sessionId</c> from the SDK on the way out so
/// the report carries the transport mode actually chosen for the session.
/// </summary>
internal sealed class ProtocolCheck : IScanCheck
{
    public string Id => "protocol";
    public IReadOnlyList<string> DependsOn => new[] { "auth" };
    public bool IsEnabledByDefault => true;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var client = await CheckSessionHelpers.TryGetSessionAsync(context, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new CheckOutcome(Ran: true, Data: null, Error: context.SessionError ?? "No MCP session available.");
        }

        var caps = client.ServerCapabilities;
        var instructions = client.ServerInstructions;

        var capData = new CapabilitiesData(
            Tools: caps?.Tools is null ? null : new ToolsCapabilityView(ListChanged: caps.Tools.ListChanged),
            Prompts: caps?.Prompts is null ? null : new PromptsCapabilityView(ListChanged: caps.Prompts.ListChanged),
            Resources: caps?.Resources is null
                ? null
                : new ResourcesCapabilityView(ListChanged: caps.Resources.ListChanged, Subscribe: caps.Resources.Subscribe),
            Logging: caps?.Logging is null ? null : new CapabilityFlagView(),
            Completions: caps?.Completions is null ? null : new CapabilityFlagView(),
            Tasks: CheckSessionHelpers.GetProp(caps, "Tasks") is null ? null : new CapabilityFlagView(),
            Experimental: CheckSessionHelpers.SafeNode(caps?.Experimental),
            Extensions: CheckSessionHelpers.SafeNode(CheckSessionHelpers.GetProp(caps, "Extensions")));

        var sessionId = CheckSessionHelpers.GetProp(client, "SessionId")?.ToString();

        var data = new ProtocolData(
            NegotiatedProtocolVersion: client.NegotiatedProtocolVersion,
            SessionId: sessionId,
            Capabilities: capData,
            Instructions: instructions,
            InstructionsLength: instructions?.Length,
            Meta: null);

        return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(data), Error: null);
    }

    internal sealed record ProtocolData(
        string? NegotiatedProtocolVersion,
        string? SessionId,
        CapabilitiesData Capabilities,
        string? Instructions,
        int? InstructionsLength,
        JsonNode? Meta);

    internal sealed record CapabilitiesData(
        ToolsCapabilityView? Tools,
        PromptsCapabilityView? Prompts,
        ResourcesCapabilityView? Resources,
        CapabilityFlagView? Logging,
        CapabilityFlagView? Completions,
        CapabilityFlagView? Tasks,
        JsonNode? Experimental,
        JsonNode? Extensions);
}
