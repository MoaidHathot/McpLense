using System.IO;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using McpLense.Scanning;
using McpLense.Scanning.TargetResolution;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Scanning.TargetResolution;

public class TargetsFromFileLoaderTests
{
    [Fact]
    public async Task LoadAsync_PlainUrls_ParsesAndDedupes()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, """
            # fleet
            https://a.example/mcp
              https://b.example/mcp

            https://a.example/mcp
            """);

            var servers = await TargetsFromFileLoader.LoadAsync(
                new[] { tmp }, new ScanConfig(), CancellationToken.None);

            servers.Count.ShouldBe(2);
            servers[0].Url!.ToString().ShouldBe("https://a.example/mcp");
            servers[1].Url!.ToString().ShouldBe("https://b.example/mcp");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task LoadAsync_NamedReference_ResolvesAgainstScanConfig()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, "@ec-foo\n");
            var scanConfig = new ScanConfig
            {
                Targets =
                {
                    new ScanTargetEntry
                    {
                        Name = "ec-foo",
                        Url = "https://ec.example/foo/mcp"
                    }
                }
            };

            var servers = await TargetsFromFileLoader.LoadAsync(
                new[] { tmp }, scanConfig, CancellationToken.None);

            servers.Count.ShouldBe(1);
            servers[0].Url!.ToString().ShouldBe("https://ec.example/foo/mcp");
            servers[0].Name.ShouldBe("ec-foo");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task LoadAsync_BadLine_ThrowsWithLineNumber()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, "https://ok/mcp\nnot-a-url\n");
            var ex = await Should.ThrowAsync<UserInputException>(() =>
                TargetsFromFileLoader.LoadAsync(new[] { tmp }, new ScanConfig(), CancellationToken.None));
            ex.Message.ShouldContain("line 2");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task LoadAsync_UnknownNamedRef_ThrowsActionable()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, "@does-not-exist\n");
            var ex = await Should.ThrowAsync<UserInputException>(() =>
                TargetsFromFileLoader.LoadAsync(new[] { tmp }, new ScanConfig(), CancellationToken.None));
            ex.Message.ShouldContain("does-not-exist");
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
