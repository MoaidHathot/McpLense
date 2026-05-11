using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using McpLense.UnitTests.Helpers;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth;

public class ProfileLoaderTests
{
    private static EnvironmentExpander FixedEnv(IDictionary<string, string?>? values = null)
    {
        values ??= new Dictionary<string, string?>();
        return new EnvironmentExpander(name => values.TryGetValue(name, out var value) ? value : null);
    }

    [Fact]
    public async Task LoadAsync_NoPaths_ReturnsEmpty()
    {
        var profiles = await ProfileLoader.LoadAsync([], FixedEnv(), CancellationToken.None);

        profiles.Count.ShouldBe(0);
    }

    [Fact]
    public async Task LoadAsync_SingleFile_ParsesAllProfiles()
    {
        const string json = """
        {
          "authProfiles": [
            { "name": "agent365", "auth": { "type": "interactive-browser", "clientId": "abc", "scopes": ["s/.default"] } },
            { "name": "github",   "auth": { "type": "bearer", "token": "tok" } }
          ]
        }
        """;

        using var file = new TempFile(json);

        var profiles = await ProfileLoader.LoadAsync([file.Path], FixedEnv(), CancellationToken.None);

        profiles.Count.ShouldBe(2);
        profiles[0].Name.ShouldBe("agent365");
        profiles[0].Auth.Kind.ShouldBe(AuthKind.InteractiveBrowser);
        profiles[1].Name.ShouldBe("github");
    }

    [Fact]
    public async Task LoadAsync_MissingFile_Throws()
    {
        var bogus = Path.Combine(Path.GetTempPath(), $"missing-{System.Guid.NewGuid():N}.json");

        var ex = await Should.ThrowAsync<UserInputException>(
            () => ProfileLoader.LoadAsync([bogus], FixedEnv(), CancellationToken.None));

        ex.Message.ShouldContain("was not found");
    }

    [Fact]
    public async Task LoadAsync_InvalidJson_Throws()
    {
        using var file = new TempFile("not-json");

        var ex = await Should.ThrowAsync<UserInputException>(
            () => ProfileLoader.LoadAsync([file.Path], FixedEnv(), CancellationToken.None));

        ex.Message.ShouldContain("Failed to parse profile file");
    }

    [Fact]
    public async Task LoadAsync_TopLevelArray_Throws()
    {
        using var file = new TempFile("[]");

        var ex = await Should.ThrowAsync<UserInputException>(
            () => ProfileLoader.LoadAsync([file.Path], FixedEnv(), CancellationToken.None));

        ex.Message.ShouldContain("JSON object at the root");
    }

    [Fact]
    public async Task LoadAsync_FileWithServersBlock_ThrowsWithHint()
    {
        const string json = """
        { "servers": [ { "name": "x", "command": "node" } ] }
        """;

        using var file = new TempFile(json);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => ProfileLoader.LoadAsync([file.Path], FixedEnv(), CancellationToken.None));

        ex.Message.ShouldContain("'servers'");
        ex.Message.ShouldContain("--config");
    }

    [Fact]
    public async Task LoadAsync_FileWithMcpServersBlock_ThrowsWithHint()
    {
        const string json = """
        { "mcpServers": { "x": { "command": "node" } } }
        """;

        using var file = new TempFile(json);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => ProfileLoader.LoadAsync([file.Path], FixedEnv(), CancellationToken.None));

        ex.Message.ShouldContain("mcpServers");
        ex.Message.ShouldContain("--config");
    }

    [Fact]
    public async Task LoadAsync_TwoFiles_MergesProfiles()
    {
        const string a = """
        { "authProfiles": [ { "name": "agent365", "auth": { "type": "bearer", "token": "a" } } ] }
        """;
        const string b = """
        { "authProfiles": [ { "name": "github",   "auth": { "type": "bearer", "token": "b" } } ] }
        """;

        using var fileA = new TempFile(a);
        using var fileB = new TempFile(b);

        var profiles = await ProfileLoader.LoadAsync([fileA.Path, fileB.Path], FixedEnv(), CancellationToken.None);

        profiles.Count.ShouldBe(2);
        profiles[0].Name.ShouldBe("agent365");
        profiles[1].Name.ShouldBe("github");
    }

    [Fact]
    public async Task LoadAsync_DuplicateProfileNameAcrossFiles_ThrowsWithBothPaths()
    {
        const string content = """
        { "authProfiles": [ { "name": "agent365", "auth": { "type": "bearer", "token": "x" } } ] }
        """;

        using var fileA = new TempFile(content);
        using var fileB = new TempFile(content);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => ProfileLoader.LoadAsync([fileA.Path, fileB.Path], FixedEnv(), CancellationToken.None));

        ex.Message.ShouldContain("Duplicate auth profile name 'agent365'");
        ex.Message.ShouldContain(fileA.Path);
        ex.Message.ShouldContain(fileB.Path);
    }

    [Fact]
    public async Task LoadAsync_DuplicateProfileNameWithinSingleFile_Throws()
    {
        const string json = """
        {
          "authProfiles": [
            { "name": "x", "auth": { "type": "bearer", "token": "1" } },
            { "name": "x", "auth": { "type": "bearer", "token": "2" } }
          ]
        }
        """;

        using var file = new TempFile(json);

        var ex = await Should.ThrowAsync<UserInputException>(
            () => ProfileLoader.LoadAsync([file.Path], FixedEnv(), CancellationToken.None));

        ex.Message.ShouldContain("Duplicate auth profile name 'x'");
    }

    [Fact]
    public async Task LoadAsync_DuplicateNameIsCaseInsensitive()
    {
        const string json = """
        {
          "authProfiles": [
            { "name": "Agent365", "auth": { "type": "bearer", "token": "1" } },
            { "name": "agent365", "auth": { "type": "bearer", "token": "2" } }
          ]
        }
        """;

        using var file = new TempFile(json);

        await Should.ThrowAsync<UserInputException>(
            () => ProfileLoader.LoadAsync([file.Path], FixedEnv(), CancellationToken.None));
    }
}
