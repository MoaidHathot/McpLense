using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace McpLense.Scanning.Checks;

internal sealed class ResourcesCheck : IScanCheck
{
    public string Id => "resources";
    public IReadOnlyList<string> DependsOn => new[] { "auth" };
    public bool IsEnabledByDefault => true;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var client = await CheckSessionHelpers.TryGetSessionAsync(context, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new CheckOutcome(Ran: true, Data: null, Error: context.SessionError ?? "No MCP session available.");
        }

        if (client.ServerCapabilities?.Resources is null)
        {
            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new ResourcesData(true, context.ActiveFetchedVia, null, [], [], new Dictionary<string, int>())), Error: null);
        }

        IList<McpClientResource> resources;
        IList<McpClientResourceTemplate> templates;
        try
        {
            resources = await client.ListResourcesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new ResourcesData(false, context.ActiveFetchedVia, $"{ex.GetType().Name}: {ex.Message}", [], [], new Dictionary<string, int>())), Error: null);
        }

        try
        {
            templates = await client.ListResourceTemplatesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            templates = Array.Empty<McpClientResourceTemplate>();
        }

        var items = resources.Select(MapResource).ToArray();
        var templateItems = templates.Select(MapTemplate).ToArray();
        var schemeHistogram = items
            .Where(r => !string.IsNullOrEmpty(r.UriScheme))
            .GroupBy(r => r.UriScheme!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(new ResourcesData(true, context.ActiveFetchedVia, null, items, templateItems, schemeHistogram)), Error: null);
    }

    private static ResourceEntryExtended MapResource(McpClientResource r)
    {
        var protocolResource = r.ProtocolResource;
        var uri = protocolResource?.Uri ?? r.Uri;
        var scheme = TryGetScheme(uri);

        return new ResourceEntryExtended(
            Name: r.Name,
            Title: protocolResource?.Title,
            Uri: uri,
            UriScheme: scheme,
            MimeType: protocolResource?.MimeType ?? r.MimeType,
            Size: protocolResource?.Size,
            Description: r.Description,
            Annotations: CheckSessionHelpers.SafeNode(CheckSessionHelpers.GetProp(protocolResource, "Annotations")),
            Icons: CheckSessionHelpers.SafeNode(CheckSessionHelpers.GetProp(protocolResource, "Icons")),
            Meta: CheckSessionHelpers.SafeNode(CheckSessionHelpers.GetProp(protocolResource, "Meta")));
    }

    private static ResourceTemplateEntryExtended MapTemplate(McpClientResourceTemplate t)
    {
        var protocolTemplate = t.ProtocolResourceTemplate;
        return new ResourceTemplateEntryExtended(
            Name: t.Name,
            Title: protocolTemplate?.Title,
            UriTemplate: protocolTemplate?.UriTemplate ?? t.UriTemplate,
            MimeType: protocolTemplate?.MimeType ?? t.MimeType,
            Description: t.Description,
            Meta: CheckSessionHelpers.SafeNode(CheckSessionHelpers.GetProp(protocolTemplate, "Meta")));
    }

    private static string? TryGetScheme(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }

        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return parsed.Scheme;
        }

        var colon = uri.IndexOf(':');
        return colon > 0 ? uri[..colon] : null;
    }

    internal sealed record ResourcesData(
        bool Fetched,
        string? FetchedVia,
        string? FetchError,
        IReadOnlyList<ResourceEntryExtended> Items,
        IReadOnlyList<ResourceTemplateEntryExtended> Templates,
        IReadOnlyDictionary<string, int> UriSchemeHistogram);

    internal sealed record ResourceEntryExtended(
        string? Name,
        string? Title,
        string? Uri,
        string? UriScheme,
        string? MimeType,
        long? Size,
        string? Description,
        JsonNode? Annotations,
        JsonNode? Icons,
        JsonNode? Meta);

    internal sealed record ResourceTemplateEntryExtended(
        string? Name,
        string? Title,
        string? UriTemplate,
        string? MimeType,
        string? Description,
        JsonNode? Meta);
}
