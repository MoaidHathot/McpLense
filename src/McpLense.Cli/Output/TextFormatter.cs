using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpLense.Scanning;

namespace McpLense;

internal static class TextFormatter
{
    public static string Format(object payload, JsonSerializerOptions jsonOptions) => payload switch
    {
        InspectReport report => FormatInspect(report, jsonOptions),
        ToolListReport report => FormatServerItems("tools", report.Servers, FormatTool, jsonOptions),
        ResourceListReport report => FormatServerItems("resources", report.Servers, FormatResource, jsonOptions),
        PromptListReport report => FormatServerItems("prompts", report.Servers, FormatPrompt, jsonOptions),
        ToolCallReport report => FormatToolCall(report, jsonOptions),
        ReadReport report => FormatRead(report, jsonOptions),
        PromptCallReport report => FormatPromptCall(report, jsonOptions),
        AuthSessionReport report => FormatAuthSession(report),
        AuthScanReport report => FormatAuthScan(report),
        ScanReport report => FormatScanReport(report, jsonOptions),
        ScanDiff.ScanDiffReport diff => FormatScanDiff(diff, jsonOptions),
        _ => JsonSerializer.Serialize(payload, jsonOptions)
    };

    /// <summary>
    /// Renders the new <see cref="ScanReport"/> from the IScanCheck pipeline. Each server
    /// gets a header followed by one named section per check.
    /// </summary>
    private static string FormatScanReport(ScanReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"scan: {report.Servers.Count} server(s)");

        for (var index = 0; index < report.Servers.Count; index++)
        {
            var entry = report.Servers[index];
            builder.AppendLine();
            AppendServerHeader(builder, entry.Name, entry.Transport, entry.Target);

            if (!string.IsNullOrEmpty(entry.Error))
            {
                AppendLine(builder, 1, $"error: {entry.Error}");
            }

            foreach (var (checkId, data) in entry.Checks)
            {
                AppendLine(builder, 1, $"{checkId}:");
                if (data is null)
                {
                    AppendLine(builder, 2, "(no data)");
                    continue;
                }

                // Default: pretty-print each check's JSON payload with 2-space indent. The
                // per-check structured rendering is reserved for a future polish pass;
                // pretty JSON is already readable and machine-parseable.
                foreach (var line in Indent(FormatJson(data, jsonOptions), 4))
                {
                    builder.AppendLine(line);
                }
            }

            if (entry.Timings.Count > 0)
            {
                AppendLine(builder, 1, "timings:");
                foreach (var (id, ms) in entry.Timings)
                {
                    AppendLine(builder, 2, $"{id}: {ms:F1} ms");
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatScanDiff(ScanDiff.ScanDiffReport diff, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"diff: baseline={diff.BaselineGeneratedAt:O} current={diff.CurrentGeneratedAt:O}");
        builder.AppendLine($"changed servers: {diff.Servers.Count}");

        foreach (var server in diff.Servers)
        {
            builder.AppendLine();
            AppendServerHeader(builder, server.Name, "diff", server.Target);
            AppendLine(builder, 1, $"status: {server.Status}");
            if (!string.IsNullOrEmpty(server.ServerFingerprintBefore))
            {
                AppendLine(builder, 1, $"serverFingerprintBefore: {server.ServerFingerprintBefore}");
            }
            if (!string.IsNullOrEmpty(server.ServerFingerprintAfter))
            {
                AppendLine(builder, 1, $"serverFingerprintAfter: {server.ServerFingerprintAfter}");
            }

            foreach (var (checkId, data) in server.Checks)
            {
                AppendLine(builder, 1, $"{checkId}:");
                if (data is null)
                {
                    AppendLine(builder, 2, "(no data)");
                    continue;
                }

                foreach (var line in Indent(FormatJson(data, jsonOptions), 4))
                {
                    builder.AppendLine(line);
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatNullableBool(bool? value)
        => value is null ? "null" : value.Value ? "true" : "false";

    /// <summary>
    /// Body of an auth-scan entry, shared between <c>auth-scan</c>'s top-level renderer and
    /// the auth section inside <c>audit</c>. The indent is parameterised because audit nests
    /// it under "auth:" while auth-scan emits it at the server header level.
    /// </summary>
    private static void AppendAuthScanBody(StringBuilder builder, ServerAuthScan entry, int indentBase)
    {
        AppendLine(builder, indentBase, $"classification: {entry.Classification}");
        AppendLine(builder, indentBase, $"summary: {entry.Summary}");

        if (!string.IsNullOrEmpty(entry.Error))
        {
            AppendLine(builder, indentBase, $"error: {entry.Error}");
        }

        AppendAuthScanDetails(builder, entry.Details, indentBase);

        if (entry.ProfileAttempts.Count == 0)
        {
            AppendLine(builder, indentBase, "profile attempts: (none)");
        }
        else
        {
            AppendLine(builder, indentBase, $"profile attempts: {entry.ProfileAttempts.Count}");
            foreach (var attempt in entry.ProfileAttempts)
            {
                AppendLine(builder, indentBase + 1, $"- profile: {attempt.ProfileName} [{attempt.AuthKind}]");
                AppendLine(builder, indentBase + 2, $"status: {(attempt.Success ? "ok" : "failed")}");

                if (attempt.Scopes is { Count: > 0 })
                {
                    AppendLine(builder, indentBase + 2, $"scopes: {string.Join(", ", attempt.Scopes)}");
                }

                if (!string.IsNullOrEmpty(attempt.Detail))
                {
                    AppendLine(builder, indentBase + 2, $"detail: {attempt.Detail}");
                }

                if (!string.IsNullOrEmpty(attempt.Error))
                {
                    AppendLine(builder, indentBase + 2, $"error: {attempt.Error}");
                }
            }
        }
    }

    private static string FormatAuthScan(AuthScanReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"scan: {report.Servers.Count} server(s)");

        for (var index = 0; index < report.Servers.Count; index++)
        {
            var entry = report.Servers[index];
            builder.AppendLine();
            AppendServerHeader(builder, entry.Name, entry.Transport, entry.Target);
            AppendAuthScanBody(builder, entry, indentBase: 1);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendAuthScanDetails(StringBuilder builder, AuthScanDetails details, int indent)
    {
        if (details.StatusCode is { } status)
        {
            AppendLine(builder, indent, $"probe status: {status}");
        }

        if (!string.IsNullOrEmpty(details.WwwAuthenticate))
        {
            AppendLine(builder, indent, $"www-authenticate: {details.WwwAuthenticate}");
        }

        if (!string.IsNullOrEmpty(details.ResourceMetadataUrl))
        {
            AppendLine(builder, indent, $"resource_metadata: {details.ResourceMetadataUrl}");
        }

        if (!string.IsNullOrEmpty(details.Resource))
        {
            AppendLine(builder, indent, $"resource: {details.Resource}");
        }

        if (details.Scopes is { Count: > 0 })
        {
            AppendLine(builder, indent, $"scopes_supported: {string.Join(", ", details.Scopes)}");
        }

        if (details.AuthorizationServers is { Count: > 0 })
        {
            AppendLine(builder, indent, $"authorization_servers: {string.Join(", ", details.AuthorizationServers)}");
        }

        if (details.AnonymousHandshakeSucceeded is { } anonHandshake)
        {
            AppendLine(builder, indent, $"anonymous handshake: {(anonHandshake ? "ok" : "failed")}");
            if (!anonHandshake && !string.IsNullOrEmpty(details.AnonymousHandshakeError))
            {
                AppendLine(builder, indent + 1, $"error: {details.AnonymousHandshakeError}");
            }
        }

        if (!string.IsNullOrEmpty(details.ProbeError))
        {
            AppendLine(builder, indent, $"probe error: {details.ProbeError}");
        }
    }

    private static string FormatAuthSession(AuthSessionReport report)
    {
        var builder = new StringBuilder();
        var success = report.Servers.Count(server => server.Success);
        builder.AppendLine($"{report.Action}: {success}/{report.Servers.Count} succeeded");

        for (var index = 0; index < report.Servers.Count; index++)
        {
            var entry = report.Servers[index];
            AppendLine(builder, 1, $"- {entry.Name} [{entry.Target}]");
            AppendLine(builder, 2, $"status: {(entry.Success ? "ok" : "failed")}");

            if (!string.IsNullOrEmpty(entry.Detail))
            {
                AppendLine(builder, 2, $"detail: {entry.Detail}");
            }

            if (!string.IsNullOrEmpty(entry.Error))
            {
                AppendLine(builder, 2, $"error: {entry.Error}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatInspect(InspectReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < report.Servers.Count; index++)
        {
            var server = report.Servers[index];
            AppendServerHeader(builder, server.Name, server.Transport, server.Target);

            if (server.Error is not null)
            {
                AppendLine(builder, 1, $"error: {server.Error}");
            }
            else
            {
                AppendLine(builder, 1, $"capabilities: {FormatCapabilities(server.Capabilities)}");
                AppendSection(builder, "tools", server.Tools, FormatTool, jsonOptions);
                AppendSection(builder, "resources", server.Resources, FormatResource, jsonOptions);
                AppendSection(builder, "resource templates", server.ResourceTemplates, FormatResourceTemplate, jsonOptions);
                AppendSection(builder, "prompts", server.Prompts, FormatPrompt, jsonOptions);
            }

            if (index < report.Servers.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatServerItems<T>(string label, IReadOnlyList<ServerItems<T>> servers, Func<T, JsonSerializerOptions, IEnumerable<string>> formatter, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < servers.Count; index++)
        {
            var server = servers[index];
            AppendServerHeader(builder, server.Name, server.Transport, server.Target);

            if (server.Error is not null)
            {
                AppendLine(builder, 1, $"error: {server.Error}");
            }
            else
            {
                AppendLine(builder, 1, $"{label}: {server.Items.Count}");
                foreach (var item in server.Items)
                {
                    foreach (var line in formatter(item, jsonOptions))
                    {
                        AppendLine(builder, 1, line);
                    }
                }
            }

            if (index < servers.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatToolCall(ToolCallReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();
        AppendServerHeader(builder, report.Server.Name, report.Server.Transport, report.Server.Target);
        AppendLine(builder, 1, $"tool: {report.ToolName}");
        AppendArguments(builder, report.Arguments, jsonOptions);

        if (report.Error is not null)
        {
            AppendLine(builder, 1, $"error: {report.Error}");
            return builder.ToString().TrimEnd();
        }

        AppendLine(builder, 1, $"progress events: {report.Progress.Count}");
        foreach (var progress in report.Progress)
        {
            AppendLine(builder, 1, $"- progress: {FormatProgress(progress)}");
        }

        AppendLine(builder, 1, $"is error: {report.Result?.IsError?.ToString().ToLowerInvariant() ?? "unknown"}");
        AppendContent(builder, report.Result?.Content ?? [], jsonOptions);
        AppendJsonBlock(builder, 1, "structured content", report.Result?.StructuredContent, jsonOptions);
        AppendJsonBlock(builder, 1, "meta", report.Result?.Meta, jsonOptions);
        return builder.ToString().TrimEnd();
    }

    private static string FormatRead(ReadReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();
        AppendServerHeader(builder, report.Server.Name, report.Server.Transport, report.Server.Target);
        AppendLine(builder, 1, $"resource: {report.Resource}");
        AppendArguments(builder, report.Arguments, jsonOptions);

        if (report.Error is not null)
        {
            AppendLine(builder, 1, $"error: {report.Error}");
            return builder.ToString().TrimEnd();
        }

        AppendLine(builder, 1, $"contents: {report.Result?.Contents.Count ?? 0}");
        foreach (var content in report.Result?.Contents ?? [])
        {
            foreach (var line in FormatResourceContent(content, jsonOptions))
            {
                AppendLine(builder, 1, line);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatPromptCall(PromptCallReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();
        AppendServerHeader(builder, report.Server.Name, report.Server.Transport, report.Server.Target);
        AppendLine(builder, 1, $"prompt: {report.PromptName}");
        AppendArguments(builder, report.Arguments, jsonOptions);

        if (report.Error is not null)
        {
            AppendLine(builder, 1, $"error: {report.Error}");
            return builder.ToString().TrimEnd();
        }

        if (!string.IsNullOrWhiteSpace(report.Result?.Description))
        {
            AppendLine(builder, 1, $"description: {report.Result.Description}");
        }

        AppendLine(builder, 1, $"messages: {report.Result?.Messages.Count ?? 0}");
        foreach (var message in report.Result?.Messages ?? [])
        {
            AppendLine(builder, 1, $"- role: {message.Role ?? "unknown"}");
            foreach (var line in FormatContent(message.Content, jsonOptions))
            {
                AppendLine(builder, 2, line);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendSection<T>(StringBuilder builder, string label, SectionResult<T> section, Func<T, JsonSerializerOptions, IEnumerable<string>> formatter, JsonSerializerOptions jsonOptions)
    {
        if (!section.Supported)
        {
            AppendLine(builder, 1, $"{label}: not supported");
            return;
        }

        if (section.Error is not null)
        {
            AppendLine(builder, 1, $"{label}: error: {section.Error}");
            return;
        }

        AppendLine(builder, 1, $"{label}: {section.Items.Count}");
        foreach (var item in section.Items)
        {
            foreach (var line in formatter(item, jsonOptions))
            {
                AppendLine(builder, 1, line);
            }
        }
    }

    private static IEnumerable<string> FormatTool(ToolInfo tool, JsonSerializerOptions jsonOptions)
    {
        yield return $"- {tool.Name}{FormatDescription(tool.Description)}";

        if (tool.InputSchema is not null)
        {
            yield return "  schema:";
            foreach (var line in Indent(FormatJson(tool.InputSchema, jsonOptions), 4))
            {
                yield return line;
            }
        }
    }

    private static IEnumerable<string> FormatResource(ResourceInfo resource, JsonSerializerOptions _) 
    {
        yield return $"- {resource.Name ?? "(unnamed)"}: {resource.Uri ?? "(no uri)"}{FormatMimeType(resource.MimeType)}";
        if (!string.IsNullOrWhiteSpace(resource.Description))
        {
            yield return $"  {resource.Description}";
        }
    }

    private static IEnumerable<string> FormatResourceTemplate(ResourceTemplateInfo template, JsonSerializerOptions _)
    {
        yield return $"- {template.Name ?? "(unnamed)"}: {template.UriTemplate ?? "(no template)"}{FormatMimeType(template.MimeType)}";
        if (!string.IsNullOrWhiteSpace(template.Description))
        {
            yield return $"  {template.Description}";
        }
    }

    private static IEnumerable<string> FormatPrompt(PromptInfo prompt, JsonSerializerOptions _)
    {
        yield return $"- {prompt.Name}{FormatDescription(prompt.Description)}";
        foreach (var argument in prompt.Arguments)
        {
            yield return $"  arg: {argument.Name ?? "(unnamed)"}{(argument.Required ? " (required)" : string.Empty)}{FormatDescription(argument.Description)}";
        }
    }

    private static void AppendContent(StringBuilder builder, IReadOnlyList<ContentBlockView> content, JsonSerializerOptions jsonOptions)
    {
        AppendLine(builder, 1, $"content: {content.Count}");
        foreach (var block in content)
        {
            foreach (var line in FormatContent(block, jsonOptions))
            {
                AppendLine(builder, 1, line);
            }
        }
    }

    private static IEnumerable<string> FormatContent(ContentBlockView? block, JsonSerializerOptions jsonOptions)
    {
        if (block is null)
        {
            yield break;
        }

        yield return $"- kind: {block.Kind}";

        if (!string.IsNullOrWhiteSpace(block.MimeType))
        {
            yield return $"  mime: {block.MimeType}";
        }

        if (!string.IsNullOrWhiteSpace(block.Text))
        {
            yield return "  text:";
            foreach (var line in Indent(block.Text, 4))
            {
                yield return line;
            }
        }

        if (block.ByteCount is not null)
        {
            yield return $"  bytes: {block.ByteCount}";
        }

        if (block.Resource is not null)
        {
            yield return "  resource:";
            foreach (var line in FormatResourceContent(block.Resource, jsonOptions))
            {
                yield return $"    {line}";
            }
        }

        if (block.Raw is not null)
        {
            yield return "  raw:";
            foreach (var line in Indent(FormatJson(block.Raw, jsonOptions), 4))
            {
                yield return line;
            }
        }
    }

    private static IEnumerable<string> FormatResourceContent(ResourceContentView content, JsonSerializerOptions jsonOptions)
    {
        yield return $"- kind: {content.Kind}";

        if (!string.IsNullOrWhiteSpace(content.Uri))
        {
            yield return $"  uri: {content.Uri}";
        }

        if (!string.IsNullOrWhiteSpace(content.MimeType))
        {
            yield return $"  mime: {content.MimeType}";
        }

        if (!string.IsNullOrWhiteSpace(content.Text))
        {
            yield return "  text:";
            foreach (var line in Indent(content.Text, 4))
            {
                yield return line;
            }
        }

        if (content.ByteCount is not null)
        {
            yield return $"  bytes: {content.ByteCount}";
        }

        if (content.Raw is not null)
        {
            yield return "  raw:";
            foreach (var line in Indent(FormatJson(content.Raw, jsonOptions), 4))
            {
                yield return line;
            }
        }
    }

    private static void AppendArguments(StringBuilder builder, JsonObject? arguments, JsonSerializerOptions jsonOptions)
    {
        if (arguments is null)
        {
            return;
        }

        AppendJsonBlock(builder, 1, "arguments", arguments, jsonOptions);
    }

    private static void AppendJsonBlock(StringBuilder builder, int indent, string label, JsonNode? value, JsonSerializerOptions jsonOptions)
    {
        if (value is null)
        {
            return;
        }

        AppendLine(builder, indent, $"{label}:");
        foreach (var line in Indent(FormatJson(value, jsonOptions), (indent + 1) * 2))
        {
            builder.AppendLine(line);
        }
    }

    private static void AppendServerHeader(StringBuilder builder, string name, string transport, string target)
        => builder.AppendLine($"{name} [{transport}] {target}");

    private static void AppendLine(StringBuilder builder, int indent, string text)
        => builder.AppendLine($"{new string(' ', indent * 2)}{text}");

    private static string FormatCapabilities(CapabilitySnapshot capabilities)
    {
        var names = new List<string>();
        if (capabilities.Tools)
        {
            names.Add("tools");
        }

        if (capabilities.Resources)
        {
            names.Add("resources");
        }

        if (capabilities.Prompts)
        {
            names.Add("prompts");
        }

        if (capabilities.Logging)
        {
            names.Add("logging");
        }

        if (capabilities.Completions)
        {
            names.Add("completions");
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static string FormatDescription(string? description)
        => string.IsNullOrWhiteSpace(description) ? string.Empty : $": {description}";

    private static string FormatMimeType(string? mimeType)
        => string.IsNullOrWhiteSpace(mimeType) ? string.Empty : $" [{mimeType}]";

    private static string FormatProgress(ProgressUpdate progress)
    {
        var pieces = new List<string>();
        if (progress.Progress is not null && progress.Total is not null)
        {
            pieces.Add($"{progress.Progress:0.##}/{progress.Total:0.##}");
        }
        else if (progress.Progress is not null)
        {
            pieces.Add(progress.Progress.Value.ToString("0.##"));
        }

        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            pieces.Add(progress.Message!);
        }

        pieces.Add(progress.Timestamp.ToString("u"));
        return string.Join(" | ", pieces);
    }

    private static string FormatJson(JsonNode node, JsonSerializerOptions jsonOptions)
        => node.ToJsonString(jsonOptions);

    private static IEnumerable<string> Indent(string text, int spaces)
    {
        var prefix = new string(' ', spaces);
        return text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None)
            .Select(line => prefix + line);
    }
}