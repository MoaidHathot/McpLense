using System.Text.Json.Nodes;

namespace McpLense;

internal enum ConnectionKind
{
    Stdio,
    Http
}

internal sealed record ResolvedServer(
    string Name,
    ConnectionKind Kind,
    string Target,
    string? Source,
    string? Command,
    IReadOnlyList<string> CommandArguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    Uri? Url,
    TransportPreference Transport,
    IReadOnlyDictionary<string, string> Headers,
    ResolvedAuth? Auth = null);

internal sealed record ExecutionOutcome(object Payload, bool HasErrors);

internal sealed record InspectReport(DateTimeOffset GeneratedAt, IReadOnlyList<ServerInspection> Servers);

internal sealed record ServerInspection(
    string Name,
    string Transport,
    string Target,
    CapabilitySnapshot Capabilities,
    SectionResult<ToolInfo> Tools,
    SectionResult<ResourceInfo> Resources,
    SectionResult<ResourceTemplateInfo> ResourceTemplates,
    SectionResult<PromptInfo> Prompts,
    string? Error = null);

internal sealed record CapabilitySnapshot(bool Tools, bool Resources, bool Prompts, bool Logging, bool Completions);

internal sealed record SectionResult<T>(bool Supported, IReadOnlyList<T> Items, string? Error = null);

internal sealed record ToolListReport(DateTimeOffset GeneratedAt, IReadOnlyList<ServerItems<ToolInfo>> Servers);

internal sealed record ResourceListReport(DateTimeOffset GeneratedAt, IReadOnlyList<ServerItems<ResourceInfo>> Servers);

internal sealed record PromptListReport(DateTimeOffset GeneratedAt, IReadOnlyList<ServerItems<PromptInfo>> Servers);

internal sealed record ServerItems<T>(string Name, string Transport, string Target, IReadOnlyList<T> Items, string? Error = null);

internal sealed record ToolCallReport(
    DateTimeOffset GeneratedAt,
    ServerReference Server,
    string ToolName,
    JsonObject? Arguments,
    IReadOnlyList<ProgressUpdate> Progress,
    CallResultView? Result,
    string? Error = null);

internal sealed record ReadReport(
    DateTimeOffset GeneratedAt,
    ServerReference Server,
    string Resource,
    JsonObject? Arguments,
    ReadResourceView? Result,
    string? Error = null);

internal sealed record PromptCallReport(
    DateTimeOffset GeneratedAt,
    ServerReference Server,
    string PromptName,
    JsonObject? Arguments,
    PromptResultView? Result,
    string? Error = null);

internal sealed record ServerReference(string Name, string Transport, string Target);

internal sealed record ToolInfo(string Name, string? Description, JsonNode? InputSchema);

internal sealed record ResourceInfo(string? Name, string? Uri, string? MimeType, string? Description);

internal sealed record ResourceTemplateInfo(string? Name, string? UriTemplate, string? MimeType, string? Description);

internal sealed record PromptInfo(string Name, string? Description, IReadOnlyList<PromptArgumentInfo> Arguments);

internal sealed record PromptArgumentInfo(string? Name, string? Description, bool Required);

internal sealed record ProgressUpdate(double? Progress, double? Total, string? Message, DateTimeOffset Timestamp);

internal sealed record CallResultView(
    bool? IsError,
    JsonNode? StructuredContent,
    JsonNode? Meta,
    IReadOnlyList<ContentBlockView> Content);

internal sealed record ReadResourceView(IReadOnlyList<ResourceContentView> Contents);

internal sealed record PromptResultView(string? Description, IReadOnlyList<PromptMessageView> Messages);

internal sealed record PromptMessageView(string? Role, ContentBlockView? Content);

internal sealed record ContentBlockView(
    string Kind,
    string? Text = null,
    string? MimeType = null,
    string? DataBase64 = null,
    int? ByteCount = null,
    ResourceContentView? Resource = null,
    JsonNode? Raw = null);

internal sealed record ResourceContentView(
    string Kind,
    string? Uri = null,
    string? MimeType = null,
    string? Text = null,
    string? DataBase64 = null,
    int? ByteCount = null,
    JsonNode? Raw = null);
