using System.Text.Json.Nodes;
using McpLense;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace McpLense.UnitTests.Tui;

/// <summary>
/// Tests for the C5 TUI polish features: section filters, JSON Schema preview, and
/// bookmark store. The filters are pure functions and easy to assert on; the
/// schema-preview renderer is checked via <see cref="TestConsole"/>; the bookmark store
/// is round-tripped through a tempfile to validate persistence.
/// </summary>
public class TuiPolishTests
{
    private static TestConsole NewConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = 200;
        return console;
    }

    // ---------- Filters ----------

    [Fact]
    public void FilterTools_EmptyFilter_ReturnsAll()
    {
        var items = new[]
        {
            new ToolInfo("Echo", "Echo back", null),
            new ToolInfo("Add", "Add two ints", null)
        };

        TuiApp.FilterTools(items, string.Empty).Count.ShouldBe(2);
    }

    [Fact]
    public void FilterTools_NameMatch_ReturnsSubset()
    {
        var items = new[]
        {
            new ToolInfo("EchoString", null, null),
            new ToolInfo("Add", null, null)
        };

        var filtered = TuiApp.FilterTools(items, "echo");
        filtered.Count.ShouldBe(1);
        filtered[0].Name.ShouldBe("EchoString");
    }

    [Fact]
    public void FilterTools_DescriptionMatch_ReturnsSubset()
    {
        var items = new[]
        {
            new ToolInfo("Foo", "Computes a value", null),
            new ToolInfo("Bar", "Sends a message", null)
        };

        var filtered = TuiApp.FilterTools(items, "computes");
        filtered.Count.ShouldBe(1);
        filtered[0].Name.ShouldBe("Foo");
    }

    [Fact]
    public void FilterTools_CaseInsensitive()
    {
        var items = new[] { new ToolInfo("FooBar", null, null) };
        TuiApp.FilterTools(items, "FOOBAR").Count.ShouldBe(1);
    }

    [Fact]
    public void FilterResources_MatchesByUri()
    {
        var items = new[]
        {
            new ResourceInfo("readme", "file://README.md", "text/markdown", null),
            new ResourceInfo("license", "file://LICENSE", "text/plain", null)
        };

        TuiApp.FilterResources(items, "readme").Count.ShouldBe(1);
        TuiApp.FilterResources(items, "license").Count.ShouldBe(1);
    }

    [Fact]
    public void FilterResourceTemplates_MatchesByTemplate()
    {
        var items = new[]
        {
            new ResourceTemplateInfo("Articles", "docs://articles/{id}", "text/markdown", null),
            new ResourceTemplateInfo("Users", "data://users/{id}", "application/json", null)
        };

        TuiApp.FilterResourceTemplates(items, "docs").Count.ShouldBe(1);
        TuiApp.FilterResourceTemplates(items, "users").Count.ShouldBe(1);
    }

    [Fact]
    public void FilterPrompts_MatchesByName()
    {
        var items = new[]
        {
            new PromptInfo("CodeReview", null, []),
            new PromptInfo("Summarise", null, [])
        };

        TuiApp.FilterPrompts(items, "review").Count.ShouldBe(1);
    }

    // ---------- Filter-aware rendering ----------

    [Fact]
    public void RenderTools_WithFilter_OnlyMatchingRows()
    {
        var console = NewConsole();
        var server = BuildServerWithTools(
            new ToolInfo("Echo", "echo it", null),
            new ToolInfo("Add", "add ints", null));

        TuiApp.RenderTools(console, server, "add");

        var output = console.Output;
        output.ShouldContain("Add");
        output.ShouldNotContain("Echo");
        output.ShouldContain("filter:");
        output.ShouldContain("1/2");
    }

    [Fact]
    public void RenderTools_NoFilter_ShowsTotalCount()
    {
        var console = NewConsole();
        var server = BuildServerWithTools(new ToolInfo("Echo", null, null));

        TuiApp.RenderTools(console, server, string.Empty);

        console.Output.ShouldContain("1 item(s)");
    }

    // ---------- Tool detail / schema preview ----------

    [Fact]
    public void RenderToolDetail_NoSchema_EmitsPlaceholder()
    {
        var console = NewConsole();
        var tool = new ToolInfo("Echo", "Echoes back", null);

        TuiApp.RenderToolDetail(console, tool);

        console.Output.ShouldContain("Echo");
        console.Output.ShouldContain("No input schema declared.");
    }

    [Fact]
    public void RenderToolDetail_WithObjectSchema_RendersPropertiesTree()
    {
        var console = NewConsole();
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "message": { "type": "string", "description": "The greeting" },
            "count":   { "type": "integer" }
          },
          "required": ["message"]
        }
        """);
        var tool = new ToolInfo("Echo", "Echo back", schema);

        TuiApp.RenderToolDetail(console, tool);

        var output = console.Output;
        output.ShouldContain("inputSchema");
        output.ShouldContain("message");
        output.ShouldContain("string");
        output.ShouldContain("count");
        output.ShouldContain("integer");
        output.ShouldContain("The greeting");
    }

    // ---------- Bookmark store ----------

    [Fact]
    public void Bookmarks_InMemory_TogglesAddRemove()
    {
        var store = TuiBookmarkStore.InMemory();
        var bookmark = new TuiBookmark("alpha", TuiBookmarkKind.Tool, "Echo");

        store.Contains(bookmark).ShouldBeFalse();
        store.Toggle(bookmark).ShouldBeTrue("added on first toggle");
        store.Contains(bookmark).ShouldBeTrue();
        store.Toggle(bookmark).ShouldBeFalse("removed on second toggle");
        store.Contains(bookmark).ShouldBeFalse();
    }

    [Fact]
    public void Bookmarks_ForServer_ScopesByServerName()
    {
        var store = TuiBookmarkStore.InMemory();
        store.Toggle(new TuiBookmark("alpha", TuiBookmarkKind.Tool, "Echo"));
        store.Toggle(new TuiBookmark("beta", TuiBookmarkKind.Prompt, "Review"));

        store.ForServer("alpha").Count.ShouldBe(1);
        store.ForServer("beta").Count.ShouldBe(1);
        store.ForServer("gamma").Count.ShouldBe(0);
    }

    [Fact]
    public void Bookmarks_LoadFromFile_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcplense-bookmarks-{Guid.NewGuid():N}.json");
        try
        {
            var first = TuiBookmarkStore.Load(path);
            first.Toggle(new TuiBookmark("alpha", TuiBookmarkKind.Tool, "Echo"));
            first.Toggle(new TuiBookmark("alpha", TuiBookmarkKind.Resource, "file://README.md"));

            // Open a second view of the same file and confirm we see both entries.
            var second = TuiBookmarkStore.Load(path);
            second.All.Count.ShouldBe(2);
            second.Contains(new TuiBookmark("alpha", TuiBookmarkKind.Tool, "Echo")).ShouldBeTrue();
            second.Contains(new TuiBookmark("alpha", TuiBookmarkKind.Resource, "file://README.md")).ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Bookmarks_LoadFromMalformedFile_RecoversAsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcplense-bookmarks-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{not json");

            var store = TuiBookmarkStore.Load(path);
            store.All.Count.ShouldBe(0);

            // After recovery, writes still go through.
            store.Toggle(new TuiBookmark("alpha", TuiBookmarkKind.Tool, "Echo"));
            store.Contains(new TuiBookmark("alpha", TuiBookmarkKind.Tool, "Echo")).ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Bookmarks_LoadMissingFile_ReturnsEmptyStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcplense-bookmarks-missing-{Guid.NewGuid():N}.json");
        var store = TuiBookmarkStore.Load(path);
        store.All.Count.ShouldBe(0);
    }

    [Fact]
    public void Bookmarks_DefaultPath_IsPerUserLocation()
    {
        var path = TuiBookmarkStore.DefaultPath();
        path.ShouldNotBeNullOrEmpty();
        path.ShouldEndWith("tui-bookmarks.json");
    }

    // ---------- Helpers ----------

    private static ServerInspection BuildServerWithTools(params ToolInfo[] tools)
        => new(
            Name: "alpha",
            Transport: "stdio",
            Target: "dotnet exec foo.dll",
            Capabilities: new CapabilitySnapshot(true, false, false, false, false),
            Tools: new SectionResult<ToolInfo>(true, tools),
            Resources: new SectionResult<ResourceInfo>(false, []),
            ResourceTemplates: new SectionResult<ResourceTemplateInfo>(false, []),
            Prompts: new SectionResult<PromptInfo>(false, []));
}
