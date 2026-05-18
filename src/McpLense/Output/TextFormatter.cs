using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        AuditReport report => FormatAudit(report, jsonOptions),
        _ => JsonSerializer.Serialize(payload, jsonOptions)
    };

    private static string FormatAudit(AuditReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"audit: {report.Servers.Count} server(s)");

        for (var index = 0; index < report.Servers.Count; index++)
        {
            var entry = report.Servers[index];
            builder.AppendLine();
            AppendServerHeader(builder, entry.Name, entry.Transport, entry.Target);

            if (!string.IsNullOrEmpty(entry.Error))
            {
                AppendLine(builder, 1, $"error: {entry.Error}");
            }

            // Auth section reuses the auth-scan rendering so the two commands look the same
            // wherever they overlap.
            AppendLine(builder, 1, "auth:");
            AppendAuthScanBody(builder, entry.Auth, indentBase: 2);

            // serverInfo (A)
            if (entry.ServerInfo is { } info)
            {
                AppendLine(builder, 1, "serverInfo:");
                if (!string.IsNullOrEmpty(info.Name)) AppendLine(builder, 2, $"name: {info.Name}");
                if (!string.IsNullOrEmpty(info.Title)) AppendLine(builder, 2, $"title: {info.Title}");
                if (!string.IsNullOrEmpty(info.Version)) AppendLine(builder, 2, $"version: {info.Version}");
                if (!string.IsNullOrEmpty(info.Description)) AppendLine(builder, 2, $"description: {info.Description}");
                if (!string.IsNullOrEmpty(info.WebsiteUrl)) AppendLine(builder, 2, $"websiteUrl: {info.WebsiteUrl}");
                if (info.Meta is not null)
                {
                    AppendJsonBlock(builder, 2, "meta", info.Meta, jsonOptions);
                }
            }

            // protocol (A)
            if (entry.Protocol is { } proto)
            {
                AppendLine(builder, 1, "protocol:");
                if (!string.IsNullOrEmpty(proto.NegotiatedProtocolVersion))
                {
                    AppendLine(builder, 2, $"negotiatedProtocolVersion: {proto.NegotiatedProtocolVersion}");
                }
                AppendLine(builder, 2, "capabilities:");
                AppendCapabilities(builder, proto.Capabilities, indent: 3, jsonOptions);
                if (proto.InstructionsLength is { } len)
                {
                    AppendLine(builder, 2, $"instructions ({len} chars):");
                    foreach (var line in (proto.Instructions ?? string.Empty).Split('\n'))
                    {
                        AppendLine(builder, 3, line.TrimEnd('\r'));
                    }
                }
                if (proto.Meta is not null)
                {
                    AppendJsonBlock(builder, 2, "meta", proto.Meta, jsonOptions);
                }
            }

            // tools / prompts / resources (B + E)
            AppendListing(builder, "tools", entry.Tools, jsonOptions);
            AppendListing(builder, "prompts", entry.Prompts, jsonOptions);
            AppendResourceListing(builder, entry.Resources, jsonOptions);

            // security (F)
            AppendSecurity(builder, entry.Security, jsonOptions);

            // oauth (G)
            if (entry.OAuth is { } oauth)
            {
                AppendOAuth(builder, oauth, jsonOptions);
            }

            // behaviour (H)
            AppendBehavior(builder, entry.Behavior, jsonOptions);

            // stdio (I)
            if (entry.Stdio is { } stdio)
            {
                AppendLine(builder, 1, "stdio:");
                AppendLine(builder, 2, $"command: {stdio.Command}");
                if (stdio.Arguments.Count > 0)
                {
                    AppendLine(builder, 2, $"arguments: {string.Join(' ', stdio.Arguments)}");
                }
                if (!string.IsNullOrEmpty(stdio.WorkingDirectory))
                {
                    AppendLine(builder, 2, $"workingDirectory: {stdio.WorkingDirectory}");
                }
                if (stdio.Environment.Count > 0)
                {
                    AppendLine(builder, 2, "environment:");
                    foreach (var kv in stdio.Environment)
                    {
                        AppendLine(builder, 3, $"{kv.Key}={kv.Value}");
                    }
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendCapabilities(StringBuilder builder, CapabilitiesView caps, int indent, JsonSerializerOptions jsonOptions)
    {
        // For each sub-capability we print "<name>: declared" / "<name>: not declared" so the
        // difference between "advertised with no sub-options" and "not advertised at all"
        // stays visible in text output. JSON consumers see the same distinction via null.
        if (caps.Tools is { } tc)
        {
            AppendLine(builder, indent, $"tools: declared (listChanged={FormatNullableBool(tc.ListChanged)})");
        }
        else
        {
            AppendLine(builder, indent, "tools: not declared");
        }

        if (caps.Prompts is { } pc)
        {
            AppendLine(builder, indent, $"prompts: declared (listChanged={FormatNullableBool(pc.ListChanged)})");
        }
        else
        {
            AppendLine(builder, indent, "prompts: not declared");
        }

        if (caps.Resources is { } rc)
        {
            AppendLine(builder, indent, $"resources: declared (listChanged={FormatNullableBool(rc.ListChanged)}, subscribe={FormatNullableBool(rc.Subscribe)})");
        }
        else
        {
            AppendLine(builder, indent, "resources: not declared");
        }

        AppendLine(builder, indent, $"logging: {(caps.Logging is null ? "not declared" : "declared")}");
        AppendLine(builder, indent, $"completions: {(caps.Completions is null ? "not declared" : "declared")}");
        AppendLine(builder, indent, $"tasks: {(caps.Tasks is null ? "not declared" : "declared")}");

        if (caps.Experimental is not null)
        {
            AppendJsonBlock(builder, indent, "experimental", caps.Experimental, jsonOptions);
        }

        if (caps.Extensions is not null)
        {
            AppendJsonBlock(builder, indent, "extensions", caps.Extensions, jsonOptions);
        }
    }

    private static void AppendListing(StringBuilder builder, string label, ToolListing listing, JsonSerializerOptions jsonOptions)
    {
        AppendLine(builder, 1, $"{label}:");
        if (!listing.Fetched)
        {
            AppendLine(builder, 2, $"fetched: false");
            if (!string.IsNullOrEmpty(listing.FetchError))
            {
                AppendLine(builder, 2, $"fetchError: {listing.FetchError}");
            }
            return;
        }

        AppendLine(builder, 2, $"fetched: true (via {listing.FetchedVia})");
        AppendLine(builder, 2, $"count: {listing.Items.Count}");
        foreach (var tool in listing.Items)
        {
            AppendLine(builder, 2, $"- name: {tool.Name}");
            if (!string.IsNullOrEmpty(tool.Title)) AppendLine(builder, 3, $"title: {tool.Title}");
            if (!string.IsNullOrEmpty(tool.Description))
            {
                AppendLine(builder, 3, "description:");
                foreach (var line in tool.Description.Split('\n'))
                {
                    AppendLine(builder, 4, line.TrimEnd('\r'));
                }
            }
            if (tool.Annotations is { } ann)
            {
                AppendLine(builder, 3, $"annotations: readOnlyHint={FormatNullableBool(ann.ReadOnlyHint)}, destructiveHint={FormatNullableBool(ann.DestructiveHint)}, idempotentHint={FormatNullableBool(ann.IdempotentHint)}, openWorldHint={FormatNullableBool(ann.OpenWorldHint)}");
            }
            else
            {
                AppendLine(builder, 3, "annotations: (none declared)");
            }
            if (tool.MissingAnnotations.Count > 0)
            {
                AppendLine(builder, 3, $"missingAnnotations: {string.Join(", ", tool.MissingAnnotations)}");
            }
            if (tool.InputSchema is not null)
            {
                AppendJsonBlock(builder, 3, "inputSchema", tool.InputSchema, jsonOptions);
            }
            if (tool.OutputSchema is not null)
            {
                AppendJsonBlock(builder, 3, "outputSchema", tool.OutputSchema, jsonOptions);
            }
            if (tool.Meta is not null)
            {
                AppendJsonBlock(builder, 3, "meta", tool.Meta, jsonOptions);
            }
        }
    }

    private static void AppendListing(StringBuilder builder, string label, PromptListing listing, JsonSerializerOptions jsonOptions)
    {
        AppendLine(builder, 1, $"{label}:");
        if (!listing.Fetched)
        {
            AppendLine(builder, 2, "fetched: false");
            if (!string.IsNullOrEmpty(listing.FetchError))
            {
                AppendLine(builder, 2, $"fetchError: {listing.FetchError}");
            }
            return;
        }

        AppendLine(builder, 2, $"fetched: true (via {listing.FetchedVia})");
        AppendLine(builder, 2, $"count: {listing.Items.Count}");
        foreach (var prompt in listing.Items)
        {
            AppendLine(builder, 2, $"- name: {prompt.Name}");
            if (!string.IsNullOrEmpty(prompt.Title)) AppendLine(builder, 3, $"title: {prompt.Title}");
            if (!string.IsNullOrEmpty(prompt.Description))
            {
                AppendLine(builder, 3, "description:");
                foreach (var line in prompt.Description.Split('\n'))
                {
                    AppendLine(builder, 4, line.TrimEnd('\r'));
                }
            }
            foreach (var arg in prompt.Arguments)
            {
                AppendLine(builder, 3, $"arg: {arg.Name ?? "(unnamed)"}{(arg.Required ? " (required)" : string.Empty)}{FormatDescription(arg.Description)}");
            }
            if (prompt.Meta is not null)
            {
                AppendJsonBlock(builder, 3, "meta", prompt.Meta, jsonOptions);
            }
        }
    }

    private static void AppendResourceListing(StringBuilder builder, ResourceListing listing, JsonSerializerOptions jsonOptions)
    {
        AppendLine(builder, 1, "resources:");
        if (!listing.Fetched)
        {
            AppendLine(builder, 2, "fetched: false");
            if (!string.IsNullOrEmpty(listing.FetchError))
            {
                AppendLine(builder, 2, $"fetchError: {listing.FetchError}");
            }
            return;
        }

        AppendLine(builder, 2, $"fetched: true (via {listing.FetchedVia})");
        AppendLine(builder, 2, $"count: {listing.Items.Count}");
        foreach (var resource in listing.Items)
        {
            AppendLine(builder, 2, $"- name: {resource.Name ?? "(unnamed)"}");
            if (!string.IsNullOrEmpty(resource.Uri)) AppendLine(builder, 3, $"uri: {resource.Uri}");
            if (!string.IsNullOrEmpty(resource.UriScheme)) AppendLine(builder, 3, $"uriScheme: {resource.UriScheme}");
            if (!string.IsNullOrEmpty(resource.MimeType)) AppendLine(builder, 3, $"mime: {resource.MimeType}");
            if (resource.Size is { } size) AppendLine(builder, 3, $"size: {size}");
            if (!string.IsNullOrEmpty(resource.Description)) AppendLine(builder, 3, $"description: {resource.Description}");
            if (resource.Meta is not null)
            {
                AppendJsonBlock(builder, 3, "meta", resource.Meta, jsonOptions);
            }
        }

        if (listing.Templates.Count > 0)
        {
            AppendLine(builder, 2, $"templates: {listing.Templates.Count}");
            foreach (var template in listing.Templates)
            {
                AppendLine(builder, 2, $"- name: {template.Name ?? "(unnamed)"}");
                if (!string.IsNullOrEmpty(template.UriTemplate)) AppendLine(builder, 3, $"uriTemplate: {template.UriTemplate}");
                if (!string.IsNullOrEmpty(template.MimeType)) AppendLine(builder, 3, $"mime: {template.MimeType}");
                if (!string.IsNullOrEmpty(template.Description)) AppendLine(builder, 3, $"description: {template.Description}");
            }
        }
    }

    private static void AppendSecurity(StringBuilder builder, SecuritySummary security, JsonSerializerOptions jsonOptions)
    {
        AppendLine(builder, 1, "security:");
        AppendLine(builder, 2, $"mixedContent: {security.MixedContent.ToString().ToLowerInvariant()}");

        if (security.Tls is { } tls)
        {
            AppendLine(builder, 2, "tls:");
            if (!string.IsNullOrEmpty(tls.Subject)) AppendLine(builder, 3, $"subject: {tls.Subject}");
            if (!string.IsNullOrEmpty(tls.Issuer)) AppendLine(builder, 3, $"issuer: {tls.Issuer}");
            if (!string.IsNullOrEmpty(tls.Thumbprint)) AppendLine(builder, 3, $"thumbprint: {tls.Thumbprint}");
            if (!string.IsNullOrEmpty(tls.SerialNumber)) AppendLine(builder, 3, $"serialNumber: {tls.SerialNumber}");
            if (tls.NotBefore is { } nb) AppendLine(builder, 3, $"notBefore: {nb:O}");
            if (tls.NotAfter is { } na) AppendLine(builder, 3, $"notAfter: {na:O}");
            if (tls.DaysUntilExpiry is { } days) AppendLine(builder, 3, $"daysUntilExpiry: {days}");
            if (!string.IsNullOrEmpty(tls.SignatureAlgorithm)) AppendLine(builder, 3, $"signatureAlgorithm: {tls.SignatureAlgorithm}");
            if (tls.SubjectAlternativeNames.Count > 0)
            {
                AppendLine(builder, 3, $"subjectAlternativeNames: {string.Join(", ", tls.SubjectAlternativeNames)}");
            }
            if (!string.IsNullOrEmpty(tls.ProtocolVersion)) AppendLine(builder, 3, $"protocolVersion: {tls.ProtocolVersion}");
        }

        if (security.ResponseHeaders is { } headers)
        {
            AppendLine(builder, 2, "responseHeaders:");
            if (!string.IsNullOrEmpty(headers.Server)) AppendLine(builder, 3, $"server: {headers.Server}");
            if (!string.IsNullOrEmpty(headers.XPoweredBy)) AppendLine(builder, 3, $"xPoweredBy: {headers.XPoweredBy}");
            if (!string.IsNullOrEmpty(headers.StrictTransportSecurity)) AppendLine(builder, 3, $"strictTransportSecurity: {headers.StrictTransportSecurity}");
            if (!string.IsNullOrEmpty(headers.ContentSecurityPolicy)) AppendLine(builder, 3, $"contentSecurityPolicy: {headers.ContentSecurityPolicy}");
            if (!string.IsNullOrEmpty(headers.XFrameOptions)) AppendLine(builder, 3, $"xFrameOptions: {headers.XFrameOptions}");
            if (!string.IsNullOrEmpty(headers.XContentTypeOptions)) AppendLine(builder, 3, $"xContentTypeOptions: {headers.XContentTypeOptions}");
            if (!string.IsNullOrEmpty(headers.ReferrerPolicy)) AppendLine(builder, 3, $"referrerPolicy: {headers.ReferrerPolicy}");
            if (!string.IsNullOrEmpty(headers.AccessControlAllowOrigin)) AppendLine(builder, 3, $"accessControlAllowOrigin: {headers.AccessControlAllowOrigin}");
            if (!string.IsNullOrEmpty(headers.AccessControlAllowCredentials)) AppendLine(builder, 3, $"accessControlAllowCredentials: {headers.AccessControlAllowCredentials}");
            if (!string.IsNullOrEmpty(headers.CacheControl)) AppendLine(builder, 3, $"cacheControl: {headers.CacheControl}");
            if (headers.Other.Count > 0)
            {
                AppendLine(builder, 3, $"other ({headers.Other.Count}):");
                foreach (var (k, v) in headers.Other)
                {
                    AppendLine(builder, 4, $"{k}: {v}");
                }
            }
        }
    }

    private static void AppendOAuth(StringBuilder builder, OAuthSummary oauth, JsonSerializerOptions jsonOptions)
    {
        AppendLine(builder, 1, "oauth:");
        if (oauth.DcrFromResourceMetadata is { } dcr)
        {
            AppendLine(builder, 2, "dcr:");
            if (!string.IsNullOrEmpty(dcr.Endpoint)) AppendLine(builder, 3, $"endpoint: {dcr.Endpoint}");
            if (dcr.OpenRegistration is { } open) AppendLine(builder, 3, $"openRegistration: {open.ToString().ToLowerInvariant()}");
        }

        if (oauth.AuthorizationServers.Count == 0)
        {
            AppendLine(builder, 2, "authorizationServers: (none fetched; pass --check-authorization-servers to fetch)");
            return;
        }

        AppendLine(builder, 2, $"authorizationServers: {oauth.AuthorizationServers.Count}");
        foreach (var server in oauth.AuthorizationServers)
        {
            AppendLine(builder, 2, $"- issuer: {server.Issuer}");
            AppendLine(builder, 3, $"fetched: {server.Fetched.ToString().ToLowerInvariant()}");
            if (!string.IsNullOrEmpty(server.FetchError)) AppendLine(builder, 3, $"fetchError: {server.FetchError}");
            if (!string.IsNullOrEmpty(server.AuthorizationEndpoint)) AppendLine(builder, 3, $"authorizationEndpoint: {server.AuthorizationEndpoint}");
            if (!string.IsNullOrEmpty(server.TokenEndpoint)) AppendLine(builder, 3, $"tokenEndpoint: {server.TokenEndpoint}");
            if (!string.IsNullOrEmpty(server.RegistrationEndpoint)) AppendLine(builder, 3, $"registrationEndpoint: {server.RegistrationEndpoint}");
            if (!string.IsNullOrEmpty(server.IntrospectionEndpoint)) AppendLine(builder, 3, $"introspectionEndpoint: {server.IntrospectionEndpoint}");
            if (!string.IsNullOrEmpty(server.RevocationEndpoint)) AppendLine(builder, 3, $"revocationEndpoint: {server.RevocationEndpoint}");
            if (!string.IsNullOrEmpty(server.JwksUri)) AppendLine(builder, 3, $"jwksUri: {server.JwksUri}");
            if (server.ScopesSupported.Count > 0) AppendLine(builder, 3, $"scopesSupported: {string.Join(", ", server.ScopesSupported)}");
            if (server.ResponseTypesSupported.Count > 0) AppendLine(builder, 3, $"responseTypesSupported: {string.Join(", ", server.ResponseTypesSupported)}");
            if (server.GrantTypesSupported.Count > 0) AppendLine(builder, 3, $"grantTypesSupported: {string.Join(", ", server.GrantTypesSupported)}");
            if (server.TokenEndpointAuthMethodsSupported.Count > 0) AppendLine(builder, 3, $"tokenEndpointAuthMethodsSupported: {string.Join(", ", server.TokenEndpointAuthMethodsSupported)}");
            if (server.CodeChallengeMethodsSupported.Count > 0) AppendLine(builder, 3, $"codeChallengeMethodsSupported: {string.Join(", ", server.CodeChallengeMethodsSupported)}");
            if (server.ResourceParameterSupported is { } rp) AppendLine(builder, 3, $"resourceParameterSupported: {rp.ToString().ToLowerInvariant()}");
        }
    }

    private static void AppendBehavior(StringBuilder builder, BehaviorProbes behavior, JsonSerializerOptions jsonOptions)
    {
        AppendLine(builder, 1, "behavior:");

        if (behavior.CallNonExistentTool is { } cn)
        {
            AppendLine(builder, 2, "callNonExistentTool:");
            AppendLine(builder, 3, $"attempted: {cn.Attempted.ToString().ToLowerInvariant()}");
            AppendLine(builder, 3, $"toolNameUsed: {cn.ToolNameUsed}");
            if (!string.IsNullOrEmpty(cn.FetchedVia)) AppendLine(builder, 3, $"fetchedVia: {cn.FetchedVia}");
            AppendLine(builder, 3, $"outcome: {cn.Outcome}");
            if (cn.ToolResultIsError is { } isErr) AppendLine(builder, 3, $"toolResultIsError: {isErr.ToString().ToLowerInvariant()}");
            if (!string.IsNullOrEmpty(cn.ToolResultJson))
            {
                AppendLine(builder, 3, "toolResultJson:");
                foreach (var line in cn.ToolResultJson.Split('\n'))
                {
                    AppendLine(builder, 4, line.TrimEnd('\r'));
                }
            }
            if (cn.JsonRpcErrorCode is { } code) AppendLine(builder, 3, $"jsonRpcErrorCode: {code}");
            if (!string.IsNullOrEmpty(cn.JsonRpcErrorMessage)) AppendLine(builder, 3, $"jsonRpcErrorMessage: {cn.JsonRpcErrorMessage}");
            if (cn.JsonRpcErrorData is not null) AppendJsonBlock(builder, 3, "jsonRpcErrorData", cn.JsonRpcErrorData, jsonOptions);
            if (!string.IsNullOrEmpty(cn.TransportError)) AppendLine(builder, 3, $"transportError: {cn.TransportError}");
        }
        else
        {
            AppendLine(builder, 2, "callNonExistentTool: (not attempted)");
        }
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
