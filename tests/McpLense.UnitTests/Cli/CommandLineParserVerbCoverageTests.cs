using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Cli;

/// <summary>
/// Characterization tests pinning parser behavior for the verbs/flags that were previously
/// untested, added BEFORE splitting <c>CommandLine.cs</c> into per-verb parsers so the refactor
/// cannot silently change them. Covers: diff / fetch-resource / observe / auth-scan verbs, the
/// scan-family flags, the --quiet/--verbose guard, and the @name named-target path.
/// </summary>
public class CommandLineParserVerbCoverageTests
{
    // --- diff ---------------------------------------------------------

    [Fact]
    public void Diff_TwoBaselines_PopulatesSubjectAndDiffPath()
    {
        var parsed = CommandLineParser.Parse(["diff", "before.json", "after.json"]);

        parsed.Command.ShouldBe(AppCommand.Diff);
        parsed.Subject.ShouldBe("before.json");
        parsed.DiffBaselinePath.ShouldBe("after.json");
    }

    [Fact]
    public void Diff_OneBaseline_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["diff", "before.json"]));

    [Fact]
    public void Diff_ThreeBaselines_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["diff", "a.json", "b.json", "c.json"]));

    // --- fetch-resource ----------------------------------------------

    [Fact]
    public void FetchResource_SubjectAndUrl_PopulatesBoth()
    {
        var parsed = CommandLineParser.Parse(["fetch-resource", "config://app/settings", "https://h/mcp"]);

        parsed.Command.ShouldBe(AppCommand.FetchResource);
        parsed.Subject.ShouldBe("config://app/settings");
        parsed.Target.Url!.ToString().ShouldBe("https://h/mcp");
    }

    [Fact]
    public void FetchResource_NoSubject_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["fetch-resource"]));

    // --- observe ------------------------------------------------------

    [Fact]
    public void Observe_Url_IsRecognized()
    {
        var parsed = CommandLineParser.Parse(["observe", "https://h/mcp"]);

        parsed.Command.ShouldBe(AppCommand.Observe);
        parsed.Target.Url!.ToString().ShouldBe("https://h/mcp");
    }

    [Fact]
    public void Observe_EnableDisable_AreRepeatableAndCollected()
    {
        var parsed = CommandLineParser.Parse([
            "observe", "https://h/mcp",
            "--enable", "behavior.serverInitiated",
            "--disable", "tlsChain",
            "--disable", "metrics"
        ]);

        parsed.CheckEnables.ShouldNotBeNull();
        parsed.CheckEnables!.ShouldContain("behavior.serverInitiated");
        parsed.CheckDisables.ShouldNotBeNull();
        parsed.CheckDisables!.ShouldBe(["tlsChain", "metrics"]);
    }

    [Fact]
    public void Observe_Timeout_IsParsed()
    {
        var parsed = CommandLineParser.Parse(["observe", "https://h/mcp", "--timeout", "10"]);

        parsed.Timeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    // --- auth-scan ----------------------------------------------------

    [Fact]
    public void AuthScan_Url_IsRecognized()
    {
        var parsed = CommandLineParser.Parse(["auth-scan", "https://h/mcp"]);

        parsed.Command.ShouldBe(AppCommand.AuthScan);
        parsed.Target.Url!.ToString().ShouldBe("https://h/mcp");
    }

    [Fact]
    public void AuthScan_ClassifyOnly_SetsAuthOverride()
    {
        var parsed = CommandLineParser.Parse(["auth-scan", "https://h/mcp", "--classify-only"]);

        parsed.Target.AuthOverrides.ClassifyOnly.ShouldBeTrue();
    }

    [Fact]
    public void AuthScan_DefaultScope_PopulatesCommandAndOverrides()
    {
        var parsed = CommandLineParser.Parse(["auth-scan", "https://h/mcp", "--default-scope", "api://x/.default"]);

        parsed.DefaultScope.ShouldBe("api://x/.default");
        parsed.Target.AuthOverrides.DefaultScope.ShouldBe("api://x/.default");
    }

    // --- scan-family flags -------------------------------------------

    [Fact]
    public void Scan_Baseline_PopulatesBaselinePath()
    {
        var parsed = CommandLineParser.Parse(["scan", "https://h/mcp", "--baseline", "out"]);

        parsed.BaselinePath.ShouldBe("out");
    }

    [Fact]
    public void Scan_Diff_PopulatesDiffBaselinePath()
    {
        var parsed = CommandLineParser.Parse(["scan", "https://h/mcp", "--diff", "prev.json"]);

        parsed.DiffBaselinePath.ShouldBe("prev.json");
    }

    [Fact]
    public void Scan_ParallelServers_IsParsed()
    {
        var parsed = CommandLineParser.Parse(["scan", "https://h/mcp", "--parallel-servers", "8"]);

        parsed.ParallelServers.ShouldBe(8);
    }

    [Fact]
    public void Scan_ParallelServers_NonInteger_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["scan", "https://h/mcp", "--parallel-servers", "lots"]));

    [Fact]
    public void Scan_ScanPlugin_IsRepeatable()
    {
        var parsed = CommandLineParser.Parse([
            "scan", "https://h/mcp",
            "--scan-plugin", "a.dll",
            "--scan-plugin", "b.dll"
        ]);

        parsed.ScanPlugins.ShouldBe(["a.dll", "b.dll"]);
    }

    [Fact]
    public void Scan_CheckAuthorizationServers_SetsOverride()
    {
        var parsed = CommandLineParser.Parse(["scan", "https://h/mcp", "--check-authorization-servers"]);

        parsed.Target.AuthOverrides.CheckAuthorizationServers.ShouldBeTrue();
    }

    [Fact]
    public void Scan_Quiet_SetsQuiet()
    {
        var parsed = CommandLineParser.Parse(["scan", "https://h/mcp", "--quiet"]);

        parsed.Quiet.ShouldBeTrue();
        parsed.Verbose.ShouldBeFalse();
    }

    [Fact]
    public void Scan_Verbose_SetsVerbose()
    {
        var parsed = CommandLineParser.Parse(["scan", "https://h/mcp", "--verbose"]);

        parsed.Verbose.ShouldBeTrue();
        parsed.Quiet.ShouldBeFalse();
    }

    [Fact]
    public void Scan_QuietAndVerbose_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse(["scan", "https://h/mcp", "--quiet", "--verbose"]));
        ex.Message.ShouldContain("--quiet and --verbose");
    }

    // --- @name named-target reference --------------------------------

    [Fact]
    public void NamedReference_Positional_StripsAtAndPopulatesNamedReference()
    {
        var parsed = CommandLineParser.Parse(["inspect", "@prod"]);

        parsed.Command.ShouldBe(AppCommand.Inspect);
        parsed.Target.NamedReference.ShouldBe("prod");
        parsed.Target.Url.ShouldBeNull();
    }

    [Fact]
    public void NamedReference_WithExplicitUrl_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["inspect", "@prod", "--url", "https://h/mcp"]));

    [Fact]
    public void NamedReference_WithServer_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["inspect", "@prod", "--server", "x"]));

    [Fact]
    public void NamedReference_AllowsHeaderOverlay()
    {
        var parsed = CommandLineParser.Parse(["inspect", "@prod", "--header", "x-test: 1"]);

        parsed.Target.NamedReference.ShouldBe("prod");
        parsed.Target.Headers.ShouldContainKey("x-test");
    }

    // --- analyze / findings ------------------------------------------

    [Fact]
    public void Analyze_Url_IsRecognized()
    {
        var parsed = CommandLineParser.Parse(["analyze", "https://h/mcp"]);

        parsed.Command.ShouldBe(AppCommand.Analyze);
        parsed.Target.Url!.ToString().ShouldBe("https://h/mcp");
    }

    [Fact]
    public void Analyze_FailOn_IsParsed()
    {
        var parsed = CommandLineParser.Parse(["analyze", "https://h/mcp", "--fail-on", "high"]);

        parsed.FailOn.ShouldBe("high");
    }

    [Fact]
    public void Analyze_FailOn_InvalidSeverity_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse(["analyze", "https://h/mcp", "--fail-on", "bogus"]));
        ex.Message.ShouldContain("not a severity");
    }

    [Fact]
    public void Scan_Findings_SetsFlag()
    {
        var parsed = CommandLineParser.Parse(["scan", "https://h/mcp", "--findings"]);

        parsed.Findings.ShouldBeTrue();
    }

    [Fact]
    public void Findings_OnInspect_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["inspect", "https://h/mcp", "--findings"]));

    [Fact]
    public void FailOn_OnInspect_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["inspect", "https://h/mcp", "--fail-on", "high"]));

    [Fact]
    public void Analyze_ApproveAndSince_AreParsed()
    {
        var approve = CommandLineParser.Parse(["analyze", "https://h/mcp", "--approve", "a.json"]);
        approve.ApprovePath.ShouldBe("a.json");

        var since = CommandLineParser.Parse(["analyze", "https://h/mcp", "--since", "a.json"]);
        since.SincePath.ShouldBe("a.json");
    }

    [Fact]
    public void Approve_OnScan_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["scan", "https://h/mcp", "--approve", "a.json"]));

    [Fact]
    public void Format_Sarif_IsParsed()
    {
        var parsed = CommandLineParser.Parse(["analyze", "https://h/mcp", "--format", "sarif"]);
        parsed.Format.ShouldBe(OutputFormat.Sarif);
    }

    // --- explain / call --example / markdown -------------------------

    [Fact]
    public void Explain_Url_IsRecognized()
    {
        var parsed = CommandLineParser.Parse(["explain", "https://h/mcp"]);
        parsed.Command.ShouldBe(AppCommand.Explain);
    }

    [Fact]
    public void Call_Example_SetsFlag()
    {
        var parsed = CommandLineParser.Parse(["call", "Echo", "https://h/mcp", "--example"]);
        parsed.Example.ShouldBeTrue();
    }

    [Fact]
    public void Example_OnInspect_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["inspect", "https://h/mcp", "--example"]));

    [Theory]
    [InlineData("markdown")]
    [InlineData("md")]
    public void Format_Markdown_IsParsed(string value)
    {
        var parsed = CommandLineParser.Parse(["explain", "https://h/mcp", "--format", value]);
        parsed.Format.ShouldBe(OutputFormat.Markdown);
    }

    [Fact]
    public void Doctor_Url_IsRecognized()
        => CommandLineParser.Parse(["doctor", "https://h/mcp"]).Command.ShouldBe(AppCommand.Doctor);

    [Fact]
    public void Watch_OnInspect_IsParsed()
        => CommandLineParser.Parse(["inspect", "https://h/mcp", "--watch", "5"]).WatchSeconds.ShouldBe(5);

    [Fact]
    public void Watch_NonPositive_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["inspect", "https://h/mcp", "--watch", "0"]));

    [Fact]
    public void Watch_OnCall_Throws()
        => Should.Throw<UserInputException>(() => CommandLineParser.Parse(["call", "Echo", "https://h/mcp", "--watch", "5"]));

    [Fact]
    public void Trace_IsParsed()
        => CommandLineParser.Parse(["inspect", "https://h/mcp", "--trace"]).Trace.ShouldBeTrue();

    [Fact]
    public void Serve_IsRecognized_WithoutTarget()
        => CommandLineParser.Parse(["serve"]).Command.ShouldBe(AppCommand.Serve);
}
