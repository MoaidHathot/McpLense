using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using McpLense.UnitTests.Helpers;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Mcp;

public class TargetResolverTests
{
    private static TargetOptions Direct(
        string? configPath = null,
        IReadOnlyList<string>? serverNames = null,
        IReadOnlyList<string>? profilePaths = null,
        string? displayName = null,
        System.Uri? url = null,
        TransportPreference transport = TransportPreference.Auto,
        IReadOnlyDictionary<string, string>? headers = null,
        string? command = null,
        IReadOnlyList<string>? commandArguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        AuthOverrides? authOverrides = null)
        => new(
            configPath is null ? [] : [configPath],
            serverNames ?? [],
            profilePaths ?? [],
            displayName,
            url,
            transport,
            headers ?? new Dictionary<string, string>(),
            command,
            commandArguments ?? [],
            workingDirectory,
            environment ?? new Dictionary<string, string>(),
            authOverrides ?? AuthOverrides.Empty);

    private static TargetOptions ConfigsTarget(
        IReadOnlyList<string> configPaths,
        IReadOnlyList<string>? serverNames = null,
        AuthOverrides? authOverrides = null)
        => new(
            configPaths,
            serverNames ?? [],
            [],
            null,
            null,
            TransportPreference.Auto,
            new Dictionary<string, string>(),
            null,
            [],
            null,
            new Dictionary<string, string>(),
            authOverrides ?? AuthOverrides.Empty);

    private static EnvironmentExpander FixedEnv(IDictionary<string, string?> values)
        => new(name => values.TryGetValue(name, out var value) ? value : null);

    // -------- Direct URL / stdio / no-target -------------------------------------

    [Fact]
    public async Task ResolveAsync_DirectUrl_ProducesHttpServer()
    {
        var options = Direct(url: new System.Uri("https://example.com/mcp"), displayName: "remote");

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers.Count.ShouldBe(1);
        var server = servers[0];
        server.Kind.ShouldBe(ConnectionKind.Http);
        server.Name.ShouldBe("remote");
        server.Source.ShouldBe("direct-url");
        server.Url!.ToString().ShouldStartWith("https://example.com/mcp");
        server.Auth.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_DirectUrlNoName_UsesHost()
    {
        var options = Direct(url: new System.Uri("https://example.com/mcp"));

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers[0].Name.ShouldBe("example.com");
    }

    [Fact]
    public async Task ResolveAsync_DirectStdio_ProducesStdioServerWithRenderedTarget()
    {
        var options = Direct(
            command: "npx",
            commandArguments: ["-y", "server with spaces"],
            workingDirectory: "/work",
            environment: new Dictionary<string, string> { ["FOO"] = "bar" });

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        var server = servers[0];
        server.Kind.ShouldBe(ConnectionKind.Stdio);
        server.Name.ShouldBe("npx");
        server.Source.ShouldBe("direct-stdio");
        server.Target.ShouldBe("npx -y \"server with spaces\"");
        server.WorkingDirectory.ShouldBe("/work");
        server.Environment["FOO"].ShouldBe("bar");
    }

    [Fact]
    public async Task ResolveAsync_NoTarget_Throws()
    {
        var options = Direct();

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("No target was resolved.");
    }

    // -------- Config loading (stdio-only) ----------------------------------------

    [Fact]
    public async Task ResolveAsync_ConfigMissing_Throws()
    {
        var options = Direct(configPath: Path.Combine(Path.GetTempPath(), $"missing-{System.Guid.NewGuid():N}.json"));

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("was not found");
    }

    [Fact]
    public async Task ResolveAsync_ConfigInvalidJson_Throws()
    {
        using var file = new TempFile("not-json");
        var options = Direct(configPath: file.Path);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("Failed to parse config JSON");
    }

    [Fact]
    public async Task ResolveAsync_ConfigEmpty_Throws()
    {
        using var file = new TempFile("{}");
        var options = Direct(configPath: file.Path);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("No MCP servers were found");
    }

    [Fact]
    public async Task ResolveAsync_McpServersShape_StdioOnly_IsParsed()
    {
        const string json = """
        {
          "mcpServers": {
            "everything": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-everything"]
            },
            "fs": {
              "command": "node",
              "args": ["fs.js"]
            }
          }
        }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers.Count.ShouldBe(2);
        var stdio = servers.Single(server => server.Name == "everything");
        stdio.Kind.ShouldBe(ConnectionKind.Stdio);
        stdio.Command.ShouldBe("npx");
        stdio.CommandArguments.ShouldBe(new[] { "-y", "@modelcontextprotocol/server-everything" });
    }

    [Fact]
    public async Task ResolveAsync_ServersArrayShape_IsParsed()
    {
        const string json = """
        {
          "servers": [
            { "name": "fs", "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."], "cwd": ".", "env": { "NODE_ENV": "dev" } }
          ]
        }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers.Count.ShouldBe(1);
        var server = servers[0];
        server.Name.ShouldBe("fs");
        server.Kind.ShouldBe(ConnectionKind.Stdio);
        server.Environment["NODE_ENV"].ShouldBe("dev");
        server.WorkingDirectory.ShouldNotBeNull();
    }

    [Fact]
    public async Task ResolveAsync_TopLevelArray_IsParsed()
    {
        const string json = """
        [
          { "name": "a", "command": "node" },
          { "command": "python" }
        ]
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers.Count.ShouldBe(2);
        servers[0].Name.ShouldBe("a");
        servers[1].Name.ShouldBe("server-2");
    }

    [Fact]
    public async Task ResolveAsync_SingleServerDefinition_IsAcceptedAsDefault()
    {
        const string json = """
        { "command": "node", "args": ["server.js"] }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers.Count.ShouldBe(1);
        servers[0].Name.ShouldBe("default");
    }

    [Fact]
    public async Task ResolveAsync_FilterByServerName_KeepsOnlyMatching()
    {
        const string json = """
        { "mcpServers": {
            "alpha": { "command": "node" },
            "beta":  { "command": "python" }
        }}
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path, serverNames: ["beta"]);

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers.Count.ShouldBe(1);
        servers[0].Name.ShouldBe("beta");
    }

    [Fact]
    public async Task ResolveAsync_FilterMissingServer_Throws()
    {
        const string json = """
        { "mcpServers": { "alpha": { "command": "node" } } }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path, serverNames: ["zeta"]);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("None of the requested servers were found");
    }

    [Fact]
    public async Task ResolveAsync_RelativeCwd_IsResolvedAgainstConfigDirectory()
    {
        using var dir = new TempDirectory();
        var configPath = dir.WriteFile("mcp.json", """
        { "mcpServers": { "x": { "command": "node", "cwd": "subdir" } } }
        """);

        var options = Direct(configPath: configPath);

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers[0].WorkingDirectory.ShouldBe(Path.Combine(dir.Path, "subdir"));
    }

    // -------- Phase A breaking changes: HTTP / auth / authProfiles in --config rejected ---

    [Fact]
    public async Task ResolveAsync_ConfigWithHttpServer_Throws()
    {
        const string json = """
        { "mcpServers": { "remote": { "url": "https://example.com/mcp" } } }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("HTTP MCP servers must be passed positionally");
    }

    [Fact]
    public async Task ResolveAsync_ConfigWithEndpointAlias_Throws()
    {
        const string json = """
        { "mcpServers": { "x": { "endpoint": "https://example.com/mcp" } } }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("HTTP MCP servers must be passed positionally");
    }

    [Fact]
    public async Task ResolveAsync_ConfigWithPerServerAuthBlock_Throws()
    {
        const string json = """
        {
          "mcpServers": {
            "local": {
              "command": "node",
              "auth": { "type": "bearer", "token": "abc" }
            }
          }
        }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("Per-server auth is no longer supported");
        ex.Message.ShouldContain("--profiles");
    }

    [Fact]
    public async Task ResolveAsync_ConfigContainingAuthProfilesBlock_Throws()
    {
        const string json = """
        { "authProfiles": [ { "name": "agent365", "auth": { "type": "bearer", "token": "x" } } ] }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("'authProfiles' block");
        ex.Message.ShouldContain("--profiles");
    }

    [Fact]
    public async Task ResolveAsync_ServerNeitherCommandNorUrl_Throws()
    {
        const string json = """
        { "mcpServers": { "x": { "name": "x" } } }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("must define a 'command'");
    }

    // -------- Auth overrides (ad-hoc Bearer + --no-auth) -------------------------

    [Fact]
    public async Task ResolveAsync_NoAuth_StripsAuthOnAllServers()
    {
        // Direct stdio target shouldn't have auth anyway, but --no-auth should be a no-op (not error).
        var options = Direct(command: "node", authOverrides: new AuthOverrides(NoAuth: true));

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers[0].Auth.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_AuthBearerOverride_AttachesToHttpServer()
    {
        var options = Direct(
            url: new System.Uri("https://example.com/mcp"),
            authOverrides: new AuthOverrides(Kind: AuthKind.Bearer, Token: "cli-token"));

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers[0].Auth!.Kind.ShouldBe(AuthKind.Bearer);
        servers[0].Auth!.Token.ShouldBe("cli-token");
    }

    [Fact]
    public async Task ResolveAsync_AuthBearerWithoutToken_Throws()
    {
        var options = Direct(
            url: new System.Uri("https://example.com/mcp"),
            authOverrides: new AuthOverrides(Kind: AuthKind.Bearer));

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("--auth bearer");
        ex.Message.ShouldContain("--auth-token");
    }

    [Fact]
    public async Task ResolveAsync_AuthTokenWithoutBearer_Throws()
    {
        var options = Direct(
            url: new System.Uri("https://example.com/mcp"),
            authOverrides: new AuthOverrides(Token: "stray"));

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("--auth-token requires '--auth bearer'");
    }

    [Fact]
    public async Task ResolveAsync_AuthOAuthAdHoc_Throws()
    {
        // Phase A: OAuth/InteractiveBrowser are profile-only on the CLI.
        var options = Direct(
            url: new System.Uri("https://example.com/mcp"),
            authOverrides: new AuthOverrides(Kind: AuthKind.OAuth));

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("oauth");
        ex.Message.ShouldContain("--profile");
    }

    [Fact]
    public async Task ResolveAsync_AuthInteractiveBrowserAdHoc_Throws()
    {
        var options = Direct(
            url: new System.Uri("https://example.com/mcp"),
            authOverrides: new AuthOverrides(Kind: AuthKind.InteractiveBrowser));

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("interactivebrowser");
        ex.Message.ShouldContain("--profile");
    }

    [Fact]
    public async Task ResolveAsync_AuthBearerOnDirectStdio_IsSilentlyDropped()
    {
        // Bearer ad-hoc only attaches to HTTP servers; stdio targets are left untouched.
        var options = Direct(
            command: "node",
            authOverrides: new AuthOverrides(Kind: AuthKind.Bearer, Token: "ignored"));

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers[0].Kind.ShouldBe(ConnectionKind.Stdio);
        servers[0].Auth.ShouldBeNull();
    }

    // -------- Env-var expansion in stdio configs ---------------------------------

    [Fact]
    public async Task ResolveAsync_StdioEnvValuesInConfig_AreEnvExpanded()
    {
        const string json = """
        {
          "mcpServers": {
            "local": {
              "command": "${BIN_PATH:-node}",
              "args": ["${SCRIPT}"],
              "env": { "NODE_ENV": "${MODE:-dev}" }
            }
          }
        }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);
        var expander = FixedEnv(new Dictionary<string, string?>
        {
            ["SCRIPT"] = "server.js"
            // BIN_PATH and MODE intentionally unset
        });

        var servers = await TargetResolver.ResolveAsync(options, expander, CancellationToken.None);

        servers[0].Command.ShouldBe("node");
        servers[0].CommandArguments.ShouldBe(new[] { "server.js" });
        servers[0].Environment["NODE_ENV"].ShouldBe("dev");
    }

    // -------- Phase B: multi-config (--config repeatable) ------------------------

    [Fact]
    public async Task ResolveAsync_MultipleConfigs_MergesServers()
    {
        const string a = """
        { "mcpServers": { "alpha": { "command": "node", "args": ["a.js"] } } }
        """;
        const string b = """
        { "mcpServers": { "beta":  { "command": "node", "args": ["b.js"] } } }
        """;

        using var fileA = new TempFile(a);
        using var fileB = new TempFile(b);

        var options = ConfigsTarget(new[] { fileA.Path, fileB.Path });

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers.Count.ShouldBe(2);
        servers.Select(s => s.Name).ShouldBe(new[] { "alpha", "beta" });
    }

    [Fact]
    public async Task ResolveAsync_MultipleConfigs_DuplicateNameAcrossFiles_Throws()
    {
        const string content = """
        { "mcpServers": { "alpha": { "command": "node" } } }
        """;

        using var fileA = new TempFile(content);
        using var fileB = new TempFile(content);

        var options = ConfigsTarget(new[] { fileA.Path, fileB.Path });

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("Duplicate stdio server name 'alpha'");
        ex.Message.ShouldContain(fileA.Path);
        ex.Message.ShouldContain(fileB.Path);
    }

    [Fact]
    public async Task ResolveAsync_MultipleConfigs_FilterByServerName_AppliesAcrossMergedSet()
    {
        const string a = """
        { "mcpServers": { "alpha": { "command": "node" } } }
        """;
        const string b = """
        { "mcpServers": { "beta":  { "command": "python" } } }
        """;

        using var fileA = new TempFile(a);
        using var fileB = new TempFile(b);

        var options = ConfigsTarget(
            new[] { fileA.Path, fileB.Path },
            serverNames: new[] { "beta" });

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers.Count.ShouldBe(1);
        servers[0].Name.ShouldBe("beta");
    }

    [Fact]
    public async Task ResolveAsync_MultipleConfigs_OneMissing_ThrowsOnFirstMissing()
    {
        const string a = """
        { "mcpServers": { "alpha": { "command": "node" } } }
        """;

        using var fileA = new TempFile(a);
        var bogus = Path.Combine(Path.GetTempPath(), $"missing-{System.Guid.NewGuid():N}.json");

        var options = ConfigsTarget(new[] { fileA.Path, bogus });

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("was not found");
    }

    [Fact]
    public async Task ResolveAsync_MultipleConfigs_OneInvalidJson_ThrowsWithPath()
    {
        const string a = """
        { "mcpServers": { "alpha": { "command": "node" } } }
        """;

        using var fileA = new TempFile(a);
        using var fileBad = new TempFile("not-json");

        var options = ConfigsTarget(new[] { fileA.Path, fileBad.Path });

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("Failed to parse config JSON");
        ex.Message.ShouldContain(fileBad.Path);
    }

    [Fact]
    public async Task ResolveAsync_MultipleConfigs_AllEmpty_ThrowsNoServers()
    {
        using var file1 = new TempFile("{}");
        using var file2 = new TempFile("{}");

        var options = ConfigsTarget(new[] { file1.Path, file2.Path });

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("No MCP servers");
    }
}
