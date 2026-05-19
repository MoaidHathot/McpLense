using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using McpLense.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Scanning;

/// <summary>
/// Pipeline-level tests: enable/disable resolution, topo sort, error capture, parallel tier
/// scheduling. Uses synthetic IScanCheck implementations so we never touch the network.
/// </summary>
public class ScanPipelineTests
{
    private sealed class TestCheck : IScanCheck
    {
        public TestCheck(string id, IReadOnlyList<string>? deps = null, bool enabled = true, Func<ScanContext, CancellationToken, Task<CheckOutcome>>? body = null)
        {
            Id = id;
            DependsOn = deps ?? Array.Empty<string>();
            IsEnabledByDefault = enabled;
            _body = body ?? ((_, _) => Task.FromResult(new CheckOutcome(true, JsonNode.Parse($"{{\"id\":\"{id}\"}}"), null)));
        }

        public string Id { get; }
        public IReadOnlyList<string> DependsOn { get; }
        public bool IsEnabledByDefault { get; }

        private readonly Func<ScanContext, CancellationToken, Task<CheckOutcome>> _body;

        public Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
            => _body(context, cancellationToken);
    }

    private static ResolvedServer HttpServer(string url = "https://test.example/mcp")
        => new(
            Name: "test",
            Kind: ConnectionKind.Http,
            Target: url,
            Source: "test",
            Command: null,
            CommandArguments: Array.Empty<string>(),
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            Url: new Uri(url),
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Auth: null);

    [Fact]
    public async Task Pipeline_RunsEnabledChecks_AndSkipsDisabledOnes()
    {
        var ran = new List<string>();
        var checks = new IScanCheck[]
        {
            new TestCheck("a", body: (_, _) => { ran.Add("a"); return Task.FromResult(CheckOutcome.OkNoData); }),
            new TestCheck("b", enabled: false, body: (_, _) => { ran.Add("b"); return Task.FromResult(CheckOutcome.OkNoData); }),
            new TestCheck("c", body: (_, _) => { ran.Add("c"); return Task.FromResult(CheckOutcome.OkNoData); })
        };

        var sp = new ServiceCollection().BuildServiceProvider();
        var pipeline = new ScanPipeline(checks, new ScanConfig(), sp);
        var report = await pipeline.RunAsync(new[] { HttpServer() }, TimeSpan.FromSeconds(5), CancellationToken.None);

        ran.ShouldBe(new[] { "a", "c" }, ignoreOrder: true);
        report.Servers.ShouldHaveSingleItem();
        report.Servers[0].Checks.Keys.ShouldContain("a");
        report.Servers[0].Checks.Keys.ShouldContain("c");
        report.Servers[0].Checks.Keys.ShouldNotContain("b");
    }

    [Fact]
    public async Task Pipeline_RespectsDependencyOrdering()
    {
        // c -> b -> a means c reads b's output via PriorOutputs, b reads a's.
        var ranOrder = new List<string>();
        var checks = new IScanCheck[]
        {
            new TestCheck("c", deps: new[] { "b" }, body: (ctx, _) =>
            {
                ranOrder.Add("c");
                ctx.PriorOutputs.ShouldContainKey("b");
                ctx.PriorOutputs.ShouldContainKey("a");
                return Task.FromResult(CheckOutcome.OkNoData);
            }),
            new TestCheck("b", deps: new[] { "a" }, body: (ctx, _) =>
            {
                ranOrder.Add("b");
                ctx.PriorOutputs.ShouldContainKey("a");
                return Task.FromResult(CheckOutcome.OkNoData);
            }),
            new TestCheck("a", body: (_, _) => { ranOrder.Add("a"); return Task.FromResult(CheckOutcome.OkNoData); })
        };

        var sp = new ServiceCollection().BuildServiceProvider();
        var pipeline = new ScanPipeline(checks, new ScanConfig(), sp);
        await pipeline.RunAsync(new[] { HttpServer() }, TimeSpan.FromSeconds(5), CancellationToken.None);

        ranOrder.IndexOf("a").ShouldBeLessThan(ranOrder.IndexOf("b"));
        ranOrder.IndexOf("b").ShouldBeLessThan(ranOrder.IndexOf("c"));
    }

    [Fact]
    public async Task Pipeline_CapturesCheckExceptions_DoesNotPoisonOtherChecks()
    {
        var checks = new IScanCheck[]
        {
            new TestCheck("good", body: (_, _) => Task.FromResult(new CheckOutcome(true, JsonNode.Parse("{\"ok\":true}"), null))),
            new TestCheck("bad", body: (_, _) => throw new InvalidOperationException("synthetic")),
            new TestCheck("alsoGood", body: (_, _) => Task.FromResult(new CheckOutcome(true, JsonNode.Parse("{\"ok\":true}"), null)))
        };

        var sp = new ServiceCollection().BuildServiceProvider();
        var pipeline = new ScanPipeline(checks, new ScanConfig(), sp);
        var report = await pipeline.RunAsync(new[] { HttpServer() }, TimeSpan.FromSeconds(5), CancellationToken.None);

        // The bad check's exception is captured into its check entry, NOT thrown out.
        var entry = report.Servers[0];
        entry.Checks["bad"]!.AsObject()["error"]!.GetValue<string>().ShouldContain("synthetic");
        entry.Checks["good"]!.AsObject()["ok"]!.GetValue<bool>().ShouldBeTrue();
        entry.Checks["alsoGood"]!.AsObject()["ok"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Pipeline_CliEnableOverridesDisabledDefault()
    {
        var ran = new List<string>();
        var checks = new IScanCheck[]
        {
            new TestCheck("disabledByDefault", enabled: false, body: (_, _) =>
            {
                ran.Add("disabledByDefault");
                return Task.FromResult(CheckOutcome.OkNoData);
            })
        };

        var sp = new ServiceCollection().BuildServiceProvider();
        var pipeline = new ScanPipeline(checks, new ScanConfig(), sp,
            cliEnables: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "disabledByDefault" });
        await pipeline.RunAsync(new[] { HttpServer() }, TimeSpan.FromSeconds(5), CancellationToken.None);

        ran.ShouldContain("disabledByDefault");
    }

    [Fact]
    public async Task Pipeline_CliDisableOverridesEnabledDefault()
    {
        var ran = new List<string>();
        var checks = new IScanCheck[]
        {
            new TestCheck("enabledByDefault", body: (_, _) =>
            {
                ran.Add("enabledByDefault");
                return Task.FromResult(CheckOutcome.OkNoData);
            })
        };

        var sp = new ServiceCollection().BuildServiceProvider();
        var pipeline = new ScanPipeline(checks, new ScanConfig(), sp,
            cliDisables: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "enabledByDefault" });
        await pipeline.RunAsync(new[] { HttpServer() }, TimeSpan.FromSeconds(5), CancellationToken.None);

        ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pipeline_RecordsPerCheckTimings()
    {
        var checks = new IScanCheck[]
        {
            new TestCheck("delayed", body: async (_, _) =>
            {
                await Task.Delay(50);
                return CheckOutcome.OkNoData;
            }),
            new TestCheck("fast", body: (_, _) => Task.FromResult(CheckOutcome.OkNoData))
        };

        var sp = new ServiceCollection().BuildServiceProvider();
        var pipeline = new ScanPipeline(checks, new ScanConfig(), sp);
        var report = await pipeline.RunAsync(new[] { HttpServer() }, TimeSpan.FromSeconds(5), CancellationToken.None);

        var timings = report.Servers[0].Timings;
        timings.ShouldContainKey("delayed");
        timings.ShouldContainKey("fast");
        // 'delayed' MUST be slower than 'fast'. Loose bound so a slow CI runner doesn't flake.
        timings["delayed"].ShouldBeGreaterThan(timings["fast"]);
    }

    [Fact]
    public async Task Pipeline_ParallelServers_RunsConcurrently_AndPreservesInputOrder()
    {
        // Four servers, each with a single check that sleeps 100ms. With parallel=4 we
        // expect wall-clock around 100-200ms (depends on test-runner overhead) instead of
        // ~400ms sequential. We assert "well under sequential" rather than a tight number
        // so CI runners don't flake.
        var checks = new IScanCheck[]
        {
            new TestCheck("slow", body: async (_, _) =>
            {
                await Task.Delay(100);
                return CheckOutcome.OkNoData;
            })
        };

        var servers = new[]
        {
            HttpServer("https://a.example/mcp") with { Name = "a" },
            HttpServer("https://b.example/mcp") with { Name = "b" },
            HttpServer("https://c.example/mcp") with { Name = "c" },
            HttpServer("https://d.example/mcp") with { Name = "d" }
        };

        var sp = new ServiceCollection().BuildServiceProvider();
        var pipeline = new ScanPipeline(checks, new ScanConfig(), sp);

        var progressNames = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = await pipeline.RunAsync(
            servers,
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            maxDegreeOfParallelism: 4,
            progress: (_, _, name, _) => progressNames.Add(name));
        sw.Stop();

        // Sequential would be >= 400ms; parallel should comfortably finish under 350.
        sw.Elapsed.TotalMilliseconds.ShouldBeLessThan(350);

        // Progress fires once per server.
        progressNames.Count.ShouldBe(4);
        progressNames.ShouldContain("a");
        progressNames.ShouldContain("d");

        // Report order matches INPUT order regardless of completion order.
        report.Servers.Select(s => s.Name).ShouldBe(new[] { "a", "b", "c", "d" });
    }
}
