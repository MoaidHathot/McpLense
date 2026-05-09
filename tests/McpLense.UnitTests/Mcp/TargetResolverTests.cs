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
        string? displayName = null,
        System.Uri? url = null,
        TransportPreference transport = TransportPreference.Auto,
        IReadOnlyDictionary<string, string>? headers = null,
        string? command = null,
        IReadOnlyList<string>? commandArguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
        => new(
            configPath,
            serverNames ?? [],
            displayName,
            url,
            transport,
            headers ?? new Dictionary<string, string>(),
            command,
            commandArguments ?? [],
            workingDirectory,
            environment ?? new Dictionary<string, string>());

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
    public async Task ResolveAsync_McpServersShape_IsParsed()
    {
        const string json = """
        {
          "mcpServers": {
            "everything": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-everything"]
            },
            "remote": {
              "url": "https://example.com/mcp",
              "transport": "streamable-http",
              "headers": { "Authorization": "Bearer token" }
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

        var http = servers.Single(server => server.Name == "remote");
        http.Kind.ShouldBe(ConnectionKind.Http);
        http.Transport.ShouldBe(TransportPreference.StreamableHttp);
        http.Headers["Authorization"].ShouldBe("Bearer token");
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
    public async Task ResolveAsync_ServersObjectMap_IsParsed()
    {
        const string json = """
        {
          "servers": {
            "remote": { "url": "https://example.com/mcp" }
          }
        }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers.Count.ShouldBe(1);
        servers[0].Name.ShouldBe("remote");
        servers[0].Kind.ShouldBe(ConnectionKind.Http);
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
    public async Task ResolveAsync_ServerBothCommandAndUrl_Throws()
    {
        const string json = """
        { "mcpServers": { "x": { "command": "node", "url": "https://example.com" } } }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("cannot define both");
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

        ex.Message.ShouldContain("must define either a command or a URL");
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
    public async Task ResolveAsync_ServerInvalidUrl_Throws()
    {
        const string json = """
        { "mcpServers": { "x": { "url": "::not-a-url::" } } }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("invalid URL");
    }

    [Fact]
    public async Task ResolveAsync_ServerUnknownTransport_Throws()
    {
        const string json = """
        { "mcpServers": { "x": { "url": "https://example.com", "transport": "smoke" } } }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => TargetResolver.ResolveAsync(options, CancellationToken.None));

        ex.Message.ShouldContain("Unknown transport");
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

    [Fact]
    public async Task ResolveAsync_EndpointAlias_IsHonored()
    {
        const string json = """
        { "mcpServers": { "x": { "endpoint": "https://example.com/mcp" } } }
        """;

        using var file = new TempFile(json);
        var options = Direct(configPath: file.Path);

        var servers = await TargetResolver.ResolveAsync(options, CancellationToken.None);

        servers[0].Kind.ShouldBe(ConnectionKind.Http);
        servers[0].Url!.ToString().ShouldStartWith("https://example.com/mcp");
    }
}
