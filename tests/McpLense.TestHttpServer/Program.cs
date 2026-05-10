using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using McpLense.TestServer.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpLense.TestHttpServer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var urlFile = ParseUrlFile(args);
        var requireBearerToken = ParseRequireBearer(args);
        var requireOAuth = ParseRequireOAuth(args);
        var oidcOnly = ParseOidcOnly(args);

        using var app = await StartAsync(urlFile, requireBearerToken, requireOAuth, oidcOnly);
        await app.WaitForShutdownAsync();
    }

    /// <summary>
    /// Builds and starts the HTTP MCP test server bound to <c>http://127.0.0.1:0</c>
    /// (an OS-assigned port). Suitable for both subprocess and in-process hosting in tests.
    /// When <paramref name="urlFile"/> is provided, the bound base URL is written to that file
    /// once the server is listening.
    /// </summary>
    /// <param name="urlFile">Optional file path to write the bound base URL into.</param>
    /// <param name="requireBearerToken">
    /// When non-null, every inbound MCP request must carry an <c>Authorization: Bearer &lt;value&gt;</c>
    /// header that exactly matches this token. Used by auth integration / E2E tests.
    /// </param>
    /// <param name="requireOAuth">
    /// When true, the server hosts mock OAuth Identity Provider endpoints (PRM/ASM/DCR/authorize/token)
    /// and gates every MCP request behind a token issued by that IdP. The two flags are mutually
    /// exclusive; combining them throws.
    /// </param>
    /// <param name="oidcOnly">
    /// When true (and <paramref name="requireOAuth"/> is also true), the server simulates an
    /// OIDC-only authorization server: the RFC 8414 <c>oauth-authorization-server</c> endpoint
    /// returns 404 and the metadata is served instead from <c>/.well-known/openid-configuration</c>.
    /// Used to exercise the discovery fallback ladder.
    /// </param>
    public static async Task<WebApplication> StartAsync(
        string? urlFile = null,
        string? requireBearerToken = null,
        bool requireOAuth = false,
        bool oidcOnly = false)
    {
        if (!string.IsNullOrEmpty(requireBearerToken) && requireOAuth)
        {
            throw new ArgumentException("--require-bearer and --require-oauth are mutually exclusive.");
        }

        if (oidcOnly && !requireOAuth)
        {
            throw new ArgumentException("--oidc-only requires --require-oauth.");
        }

        var builder = WebApplication.CreateBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Warning;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddHttpContextAccessor();

        if (requireOAuth)
        {
            builder.Services.AddSingleton<MockIdentityProvider>();
        }

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
                // Stateful + legacy SSE so a single instance can satisfy
                // auto, streamable-http, and SSE transport tests.
                options.Stateless = false;
#pragma warning disable MCP9004
                options.EnableLegacySse = true;
#pragma warning restore MCP9004
            })
            .WithTools<EchoTools>()
            .WithTools<MathTools>()
            .WithTools<LongRunningTools>()
            .WithTools<FailingTools>()
            .WithTools<HeaderTools>()
            .WithResources<TestResources>()
            .WithPrompts<TestPrompts>();

        var app = builder.Build();

        if (!string.IsNullOrEmpty(requireBearerToken))
        {
            app.Use(async (context, next) =>
            {
                // The MCP SDK's MapMcp registers at root by default ("/" + "/sse"), so we
                // gate every inbound request rather than path-filtering. This server is a
                // single-purpose MCP host in tests, so a global gate is correct.
                var authorization = context.Request.Headers.Authorization.ToString();
                var expected = $"Bearer {requireBearerToken}";
                if (!string.Equals(authorization, expected, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers["WWW-Authenticate"] = "Bearer";
                    await context.Response.WriteAsync("Unauthorized");
                    return;
                }

                await next();
            });
        }

        if (requireOAuth)
        {
            ConfigureOAuth(app, oidcOnly);
        }

        app.MapMcp();

        await app.StartAsync();

        if (!string.IsNullOrWhiteSpace(urlFile))
        {
            var baseUrl = app.Urls.First();
            var directory = Path.GetDirectoryName(Path.GetFullPath(urlFile));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(urlFile, baseUrl);
        }

        return app;
    }

    /// <summary>
    /// Wires up the mock OAuth Identity Provider (PRM/ASM/DCR/authorize/token) and the bearer
    /// gate that requires every MCP request to carry an IdP-issued access token. When
    /// <paramref name="oidcOnly"/> is true, the RFC 8414 endpoint is suppressed (returns 404)
    /// and the same metadata is published at <c>/.well-known/openid-configuration</c> instead,
    /// simulating Microsoft Entra ID v2.0 and other OIDC-only authorization servers.
    /// </summary>
    private static void ConfigureOAuth(WebApplication app, bool oidcOnly)
    {
        var idp = app.Services.GetRequiredService<MockIdentityProvider>();

        // --- Bearer gate: skip well-known + oauth paths, require valid token elsewhere. -----
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/.well-known/", StringComparison.Ordinal) ||
                path.StartsWith("/oauth/", StringComparison.Ordinal))
            {
                await next();
                return;
            }

            var authorization = context.Request.Headers.Authorization.ToString();
            if (string.IsNullOrEmpty(authorization) ||
                !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["WWW-Authenticate"] =
                    $"Bearer resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource\"";
                await context.Response.WriteAsync("Unauthorized");
                return;
            }

            var token = authorization["Bearer ".Length..];
            if (!idp.IsValidAccessToken(token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["WWW-Authenticate"] = "Bearer error=\"invalid_token\"";
                await context.Response.WriteAsync("Invalid token");
                return;
            }

            await next();
        });

        // --- Protected Resource Metadata (RFC 9728) ----------------------------------------
        app.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx) =>
        {
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Json(new
            {
                resource = baseUrl + "/",
                authorization_servers = new[] { baseUrl },
                scopes_supported = new[] { "mcp.read", "mcp.write" },
                bearer_methods_supported = new[] { "header" }
            });
        });

        // --- Authorization Server Metadata. In normal mode we publish the RFC 8414 document.
        //     In OIDC-only mode we suppress it (404) and publish the same fields under the
        //     OIDC well-known URL instead, modelling Microsoft Entra ID v2.0.
        if (!oidcOnly)
        {
            app.MapGet("/.well-known/oauth-authorization-server", (HttpContext ctx) =>
                Results.Json(BuildAuthorizationServerMetadata(ctx)));
        }
        else
        {
            app.MapGet("/.well-known/openid-configuration", (HttpContext ctx) =>
                Results.Json(BuildAuthorizationServerMetadata(ctx)));
        }

        // --- Dynamic Client Registration (RFC 7591) ----------------------------------------
        app.MapPost("/oauth/register", async (HttpContext ctx) =>
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            var clientId = idp.RegisterClient(body);
            return Results.Json(new
            {
                client_id = clientId,
                client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, statusCode: StatusCodes.Status201Created);
        });

        // --- Authorization endpoint --------------------------------------------------------
        app.MapGet("/oauth/authorize", (HttpContext ctx) =>
        {
            var query = ctx.Request.Query;
            var clientId = query["client_id"].ToString();
            var redirectUri = query["redirect_uri"].ToString();
            var state = query["state"].ToString();
            var codeChallenge = query["code_challenge"].ToString();
            var method = query["code_challenge_method"].ToString();
            var resource = query["resource"].ToString();
            var scope = query["scope"].ToString();

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri) ||
                string.IsNullOrEmpty(state) || string.IsNullOrEmpty(codeChallenge) ||
                !string.Equals(method, "S256", StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "invalid_request" });
            }

            var code = idp.IssueAuthorizationCode(clientId, redirectUri, codeChallenge, resource, scope);

            // Redirect to the loopback redirect URI with the issued code + state.
            var separator = redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var location = $"{redirectUri}{separator}code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}";
            return Results.Redirect(location);
        });

        // --- Token endpoint ----------------------------------------------------------------
        app.MapPost("/oauth/token", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var grant = form["grant_type"].ToString();

            if (grant == "authorization_code")
            {
                var code = form["code"].ToString();
                var verifier = form["code_verifier"].ToString();
                var clientId = form["client_id"].ToString();
                var redirectUri = form["redirect_uri"].ToString();

                var token = idp.ExchangeAuthorizationCode(code, clientId, redirectUri, verifier);
                if (token is null)
                {
                    return Results.BadRequest(new { error = "invalid_grant" });
                }

                return Results.Json(new
                {
                    access_token = token.AccessToken,
                    refresh_token = token.RefreshToken,
                    token_type = "Bearer",
                    expires_in = (int)MockIdentityProvider.AccessTokenLifetime.TotalSeconds,
                    scope = token.Scope
                });
            }

            if (grant == "refresh_token")
            {
                var refreshToken = form["refresh_token"].ToString();
                var clientId = form["client_id"].ToString();

                var token = idp.RefreshAccessToken(refreshToken, clientId);
                if (token is null)
                {
                    return Results.BadRequest(new { error = "invalid_grant" });
                }

                return Results.Json(new
                {
                    access_token = token.AccessToken,
                    refresh_token = token.RefreshToken,
                    token_type = "Bearer",
                    expires_in = (int)MockIdentityProvider.AccessTokenLifetime.TotalSeconds,
                    scope = token.Scope
                });
            }

            return Results.BadRequest(new { error = "unsupported_grant_type" });
        });
    }

    private static object BuildAuthorizationServerMetadata(HttpContext ctx)
    {
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        return new
        {
            issuer = baseUrl,
            authorization_endpoint = baseUrl + "/oauth/authorize",
            token_endpoint = baseUrl + "/oauth/token",
            registration_endpoint = baseUrl + "/oauth/register",
            code_challenge_methods_supported = new[] { "S256" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            response_types_supported = new[] { "code" },
            token_endpoint_auth_methods_supported = new[] { "none", "client_secret_basic" },
            scopes_supported = new[] { "mcp.read", "mcp.write" }
        };
    }

    private static string? ParseUrlFile(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--url-file" && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string? ParseRequireBearer(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--require-bearer" && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool ParseRequireOAuth(string[] args)
    {
        return args.Any(a => string.Equals(a, "--require-oauth", StringComparison.Ordinal));
    }

    private static bool ParseOidcOnly(string[] args)
    {
        return args.Any(a => string.Equals(a, "--oidc-only", StringComparison.Ordinal));
    }
}

[McpServerToolType]
public sealed class HeaderTools
{
    [McpServerTool(Name = "GetHeader"), Description("Returns the value of an inbound HTTP request header.")]
    public static string GetHeader(
        IHttpContextAccessor httpContextAccessor,
        [Description("Header name to read")] string name)
    {
        var headers = httpContextAccessor.HttpContext?.Request.Headers;
        if (headers is null)
        {
            return "<no-context>";
        }

        return headers.TryGetValue(name, out var value)
            ? value.ToString()
            : "<missing>";
    }
}

/// <summary>
/// Minimal in-memory OAuth 2.1 + PKCE + DCR identity provider used by tests. State (clients,
/// codes, tokens) is held in concurrent dictionaries; nothing is persisted across server runs.
/// </summary>
public sealed class MockIdentityProvider
{
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(5);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RegisteredClient> _clients = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AuthorizationCodeEntry> _codes = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IssuedToken> _tokens = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _refreshTokens = new();

    public string RegisterClient(string requestBody)
    {
        // The body conforms to RFC 7591 client metadata. We don't validate fields exhaustively;
        // tests simply need a deterministic client_id back.
        var clientId = "dcr-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        _clients[clientId] = new RegisteredClient(clientId, requestBody);
        return clientId;
    }

    public string IssueAuthorizationCode(string clientId, string redirectUri, string codeChallenge, string resource, string scope)
    {
        var code = "code-" + Guid.NewGuid().ToString("N").Substring(0, 16);
        _codes[code] = new AuthorizationCodeEntry(clientId, redirectUri, codeChallenge, resource, scope, DateTimeOffset.UtcNow);
        return code;
    }

    public IssuedToken? ExchangeAuthorizationCode(string code, string clientId, string redirectUri, string codeVerifier)
    {
        if (!_codes.TryRemove(code, out var entry))
        {
            return null;
        }

        if (!string.Equals(entry.ClientId, clientId, StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.Equals(entry.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            return null;
        }

        // Verify PKCE: BASE64URL(SHA256(code_verifier)) == code_challenge.
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var challenge = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!string.Equals(challenge, entry.CodeChallenge, StringComparison.Ordinal))
        {
            return null;
        }

        return IssueToken(clientId, entry.Scope);
    }

    public IssuedToken? RefreshAccessToken(string refreshToken, string clientId)
    {
        if (!_refreshTokens.TryGetValue(refreshToken, out var existingClientId))
        {
            return null;
        }

        if (!string.Equals(existingClientId, clientId, StringComparison.Ordinal))
        {
            return null;
        }

        // Rotate the refresh token (RFC 6749 best practice for public clients).
        _refreshTokens.TryRemove(refreshToken, out _);
        return IssueToken(clientId, scope: null);
    }

    public bool IsValidAccessToken(string accessToken)
    {
        if (!_tokens.TryGetValue(accessToken, out var token))
        {
            return false;
        }

        return token.IssuedAt + AccessTokenLifetime > DateTimeOffset.UtcNow;
    }

    private IssuedToken IssueToken(string clientId, string? scope)
    {
        var access = "tok-" + Guid.NewGuid().ToString("N");
        var refresh = "rt-" + Guid.NewGuid().ToString("N");
        var token = new IssuedToken(access, refresh, scope, DateTimeOffset.UtcNow);
        _tokens[access] = token;
        _refreshTokens[refresh] = clientId;
        return token;
    }

    public sealed record RegisteredClient(string ClientId, string MetadataJson);
    public sealed record AuthorizationCodeEntry(string ClientId, string RedirectUri, string CodeChallenge, string Resource, string Scope, DateTimeOffset IssuedAt);
    public sealed record IssuedToken(string AccessToken, string RefreshToken, string? Scope, DateTimeOffset IssuedAt);
}
