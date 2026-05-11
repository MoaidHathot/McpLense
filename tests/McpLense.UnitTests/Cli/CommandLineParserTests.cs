using System;
using System.Linq;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Cli;

public class CommandLineParserTests
{
    [Fact]
    public void Parse_NoArgs_ReturnsHelp()
    {
        var parsed = CommandLineParser.Parse([]);

        parsed.Command.ShouldBe(AppCommand.Help);
        parsed.Format.ShouldBe(OutputFormat.Text);
    }

    [Fact]
    public void Parse_HelpFlagAfterCommand_ReturnsHelp()
    {
        var parsed = CommandLineParser.Parse(["inspect", "--help"]);

        parsed.Command.ShouldBe(AppCommand.Help);
    }

    [Fact]
    public void Parse_HelpCommand_ReturnsHelp()
    {
        var parsed = CommandLineParser.Parse(["help"]);

        parsed.Command.ShouldBe(AppCommand.Help);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Parse_VersionToken_ReturnsVersion(string token)
    {
        var parsed = CommandLineParser.Parse([token]);

        parsed.Command.ShouldBe(AppCommand.Version);
    }

    [Theory]
    [InlineData("inspect", nameof(AppCommand.Inspect))]
    [InlineData("tools", nameof(AppCommand.Tools))]
    [InlineData("resources", nameof(AppCommand.Resources))]
    [InlineData("prompts", nameof(AppCommand.Prompts))]
    [InlineData("tui", nameof(AppCommand.Tui))]
    public void Parse_KnownCommands_AreRecognized(string verb, string expected)
    {
        var parsed = CommandLineParser.Parse([verb, "--url", "https://example.com/mcp"]);

        parsed.Command.ShouldBe(Enum.Parse<AppCommand>(expected));
    }

    [Fact]
    public void Parse_UnknownCommand_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse(["bogus"]));
        ex.Message.ShouldContain("Unknown command 'bogus'.");
    }

    [Fact]
    public void Parse_DashAlone_RequiresCommandBefore()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse(["--", "npx"]));
        ex.Message.ShouldContain("Command is required before '--'.");
    }

    [Fact]
    public void Parse_ConfigTarget_ResolvesPath()
    {
        var parsed = CommandLineParser.Parse(["inspect", "--config", "mcp.json"]);

        parsed.Target.ConfigPaths.ShouldBe(new[] { "mcp.json" });
        parsed.Target.Url.ShouldBeNull();
        parsed.Target.Command.ShouldBeNull();
    }

    [Fact]
    public void Parse_ConfigFlag_Repeatable_CollectsAllPaths()
    {
        // Phase B: --config is repeatable. Multiple stdio config files are merged at resolve time.
        var parsed = CommandLineParser.Parse([
            "inspect", "--config", "a.json", "--config", "b.json", "--config", "c.json"
        ]);

        parsed.Target.ConfigPaths.ShouldBe(new[] { "a.json", "b.json", "c.json" });
    }

    [Fact]
    public void Parse_UrlTarget_ParsesUriAndHeaders()
    {
        var parsed = CommandLineParser.Parse([
            "inspect",
            "--url", "https://example.com/mcp",
            "--header", "Authorization=Bearer token",
            "--header", "X-Trace=42",
            "--transport", "streamable-http"
        ]);

        parsed.Target.Url.ShouldNotBeNull();
        parsed.Target.Url!.ToString().ShouldStartWith("https://example.com/mcp");
        parsed.Target.Headers["Authorization"].ShouldBe("Bearer token");
        parsed.Target.Headers["X-Trace"].ShouldBe("42");
        parsed.Target.Transport.ShouldBe(TransportPreference.StreamableHttp);
    }

    [Fact]
    public void Parse_HeadersWithColonSeparator_AreAccepted()
    {
        var parsed = CommandLineParser.Parse([
            "inspect",
            "--url", "https://example.com/mcp",
            "--header", "X-Custom:value"
        ]);

        parsed.Target.Headers["X-Custom"].ShouldBe("value");
    }

    [Theory]
    [InlineData("auto", nameof(TransportPreference.Auto))]
    [InlineData("streamable-http", nameof(TransportPreference.StreamableHttp))]
    [InlineData("streamablehttp", nameof(TransportPreference.StreamableHttp))]
    [InlineData("http", nameof(TransportPreference.StreamableHttp))]
    [InlineData("sse", nameof(TransportPreference.Sse))]
    public void Parse_TransportValues_AreNormalized(string raw, string expected)
    {
        var parsed = CommandLineParser.Parse([
            "inspect",
            "--url", "https://example.com/mcp",
            "--transport", raw
        ]);

        parsed.Target.Transport.ShouldBe(Enum.Parse<TransportPreference>(expected));
    }

    [Fact]
    public void Parse_TransportInvalid_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect",
            "--url", "https://example.com/mcp",
            "--transport", "smoke-signal"
        ])).Message.ShouldContain("Unknown transport");
    }

    [Fact]
    public void Parse_StdioCommandViaOptions_Works()
    {
        var parsed = CommandLineParser.Parse([
            "inspect",
            "--command", "npx",
            "--command-arg", "-y",
            "--command-arg", "@modelcontextprotocol/server-everything",
            "--cwd", "/work",
            "--env", "NODE_ENV=test"
        ]);

        parsed.Target.Command.ShouldBe("npx");
        parsed.Target.CommandArguments.ShouldBe(new[] { "-y", "@modelcontextprotocol/server-everything" });
        parsed.Target.WorkingDirectory.ShouldBe("/work");
        parsed.Target.Environment["NODE_ENV"].ShouldBe("test");
    }

    [Fact]
    public void Parse_StdioCommandViaSeparator_Works()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--", "npx", "-y", "@modelcontextprotocol/server-everything"
        ]);

        parsed.Target.Command.ShouldBe("npx");
        parsed.Target.CommandArguments.ShouldBe(new[] { "-y", "@modelcontextprotocol/server-everything" });
    }

    [Fact]
    public void Parse_BothCommandStyles_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--command", "npx", "--", "node", "server.js"
        ])).Message.ShouldContain("not both");
    }

    [Fact]
    public void Parse_NoTarget_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse(["inspect"]))
            .Message.ShouldContain("Specify a target");
    }

    [Fact]
    public void Parse_MultipleTargets_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--config", "mcp.json", "--url", "https://example.com/mcp"
        ])).Message.ShouldContain("exactly one target");
    }

    // -------- Positional URL (Phase A) -------------------------------------------

    [Fact]
    public void Parse_PositionalUrl_OnInspect_PopulatesTarget()
    {
        var parsed = CommandLineParser.Parse(["inspect", "https://example.com/mcp"]);

        parsed.Target.Url.ShouldNotBeNull();
        parsed.Target.Url!.ToString().ShouldStartWith("https://example.com/mcp");
        parsed.Target.ConfigPaths.Count.ShouldBe(0);
    }

    [Fact]
    public void Parse_PositionalUrl_OnTools_PopulatesTarget()
    {
        var parsed = CommandLineParser.Parse(["tools", "https://example.com/mcp"]);

        parsed.Command.ShouldBe(AppCommand.Tools);
        parsed.Target.Url.ShouldNotBeNull();
    }

    [Fact]
    public void Parse_PositionalUrl_AndCallSubject_BothAccepted()
    {
        var parsed = CommandLineParser.Parse(["call", "echo", "https://example.com/mcp"]);

        parsed.Subject.ShouldBe("echo");
        parsed.Target.Url!.ToString().ShouldStartWith("https://example.com/mcp");
    }

    [Fact]
    public void Parse_PositionalUrl_AndExplicitUrl_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "https://example.com/mcp", "--url", "https://other.example.com/mcp"
        ])).Message.ShouldContain("Specify the URL positionally OR via --url");
    }

    [Fact]
    public void Parse_NonUrlPositional_OnInspect_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse(["inspect", "extra"]))
            .Message.ShouldContain("at most a single positional URL");
    }

    // -------- Profile flags (Phase A) --------------------------------------------

    [Fact]
    public void Parse_ProfilesFlag_PopulatesProfilePaths()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "https://example.com/mcp", "--profiles", "/tmp/profiles.json"
        ]);

        parsed.Target.ProfilePaths.ShouldBe(new[] { "/tmp/profiles.json" });
    }

    [Fact]
    public void Parse_ProfilesFlag_Repeatable()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "https://example.com/mcp",
            "--profiles", "a.json", "--profiles", "b.json"
        ]);

        parsed.Target.ProfilePaths.ShouldBe(new[] { "a.json", "b.json" });
    }

    [Fact]
    public void Parse_ProfileFlag_PopulatesProfile()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "https://example.com/mcp", "--profile", "agent365"
        ]);

        parsed.Target.AuthOverrides.Profile.ShouldBe("agent365");
    }

    [Fact]
    public void Parse_ProfileFlag_EnvExpanded()
    {
        const string varName = "MCPLENSE_TEST_PROFILE_NAME";
        Environment.SetEnvironmentVariable(varName, "from-env");
        try
        {
            var parsed = CommandLineParser.Parse([
                "inspect", "https://example.com/mcp", "--profile", $"env:{varName}"
            ]);

            parsed.Target.AuthOverrides.Profile.ShouldBe("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_ProfileFlag_EnvExpandsToEmpty_Throws()
    {
        const string varName = "MCPLENSE_TEST_PROFILE_EMPTY";
        Environment.SetEnvironmentVariable(varName, string.Empty);
        try
        {
            var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
                "inspect", "https://example.com/mcp", "--profile", $"env:{varName}"
            ]));

            ex.Message.ShouldContain("--profile");
            ex.Message.ShouldContain("empty value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_TryAll_IsBoolean_NoValueRequired()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "https://example.com/mcp", "--try-all"
        ]);

        parsed.Target.AuthOverrides.TryAll.ShouldBeTrue();
    }

    [Fact]
    public void Parse_TryAllAndProfile_Together_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "https://example.com/mcp", "--try-all", "--profile", "x"
        ])).Message.ShouldContain("--try-all and --profile cannot be combined");
    }

    // -------- Output format / timeout / args / progress -------------------------

    [Theory]
    [InlineData("text", nameof(OutputFormat.Text))]
    [InlineData("json", nameof(OutputFormat.Json))]
    [InlineData("dump", nameof(OutputFormat.Dumpify))]
    [InlineData("dumpify", nameof(OutputFormat.Dumpify))]
    public void Parse_FormatOption_IsParsed(string raw, string expected)
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--format", raw
        ]);

        parsed.Format.ShouldBe(Enum.Parse<OutputFormat>(expected));
    }

    [Fact]
    public void Parse_FormatShortFlag_IsParsed()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "-f", "json"
        ]);

        parsed.Format.ShouldBe(OutputFormat.Json);
    }

    [Fact]
    public void Parse_TimeoutShortFlag_IsParsed()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "-t", "5"
        ]);

        parsed.Timeout.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Parse_FormatInvalid_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--format", "xml"
        ])).Message.ShouldContain("Unknown format");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void Parse_TimeoutInvalid_Throws(string raw)
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--timeout", raw
        ])).Message.ShouldContain("Invalid timeout");
    }

    [Fact]
    public void Parse_TimeoutDefault_Is30Seconds()
    {
        var parsed = CommandLineParser.Parse(["inspect", "--url", "https://example.com/mcp"]);

        parsed.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Parse_CallRequiresSubject()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "call", "--url", "https://example.com/mcp"
        ])).Message.ShouldContain("requires a name");
    }

    [Fact]
    public void Parse_CallParsesArgsJsonAndDefaultsToObject()
    {
        var parsed = CommandLineParser.Parse([
            "call", "echo", "--url", "https://example.com/mcp", "--args", "{\"message\":\"hi\"}"
        ]);

        parsed.Subject.ShouldBe("echo");
        parsed.Arguments.ShouldNotBeNull();
        parsed.Arguments!["message"]!.GetValue<string>().ShouldBe("hi");
    }

    [Fact]
    public void Parse_CallWithoutArgs_DefaultsToEmptyObject()
    {
        var parsed = CommandLineParser.Parse(["call", "echo", "--url", "https://example.com/mcp"]);

        parsed.Arguments.ShouldNotBeNull();
        parsed.Arguments!.Count.ShouldBe(0);
    }

    [Fact]
    public void Parse_ReadWithoutArgs_KeepsArgumentsNull()
    {
        var parsed = CommandLineParser.Parse([
            "read", "config://app/settings", "--url", "https://example.com/mcp"
        ]);

        parsed.Subject.ShouldBe("config://app/settings");
        parsed.Arguments.ShouldBeNull();
    }

    [Fact]
    public void Parse_ArgsInvalidJson_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "call", "echo", "--url", "https://example.com/mcp", "--args", "not-json"
        ])).Message.ShouldContain("Invalid JSON");
    }

    [Fact]
    public void Parse_ArgsNonObject_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "call", "echo", "--url", "https://example.com/mcp", "--args", "[1,2,3]"
        ])).Message.ShouldContain("must be a JSON object");
    }

    [Fact]
    public void Parse_ArgsForListCommand_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "tools", "--url", "https://example.com/mcp", "--args", "{}"
        ])).Message.ShouldContain("--args is only valid");
    }

    [Fact]
    public void Parse_ProgressForCall_DefaultsToTrue()
    {
        var parsed = CommandLineParser.Parse([
            "call", "echo", "--url", "https://example.com/mcp"
        ]);

        parsed.ProgressEnabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    public void Parse_ProgressBooleanValues(string raw, bool expected)
    {
        var parsed = CommandLineParser.Parse([
            "call", "echo", "--url", "https://example.com/mcp", "--progress", raw
        ]);

        parsed.ProgressEnabled.ShouldBe(expected);
    }

    [Fact]
    public void Parse_ProgressInvalid_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "call", "echo", "--url", "https://example.com/mcp", "--progress", "maybe"
        ])).Message.ShouldContain("Unknown progress value");
    }

    [Fact]
    public void Parse_ProgressOutsideCall_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "tools", "--url", "https://example.com/mcp", "--progress", "true"
        ])).Message.ShouldContain("--progress is only valid");
    }

    [Fact]
    public void Parse_UnknownOption_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--mystery", "thing"
        ])).Message.ShouldContain("Unknown option '--mystery'.");
    }

    [Fact]
    public void Parse_DuplicateSingletonOption_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--format", "json", "--format", "text"
        ])).Message.ShouldContain("can only be specified once");
    }

    [Fact]
    public void Parse_OptionWithEqualsValue_Works()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url=https://example.com/mcp", "--format=json"
        ]);

        parsed.Target.Url!.ToString().ShouldStartWith("https://example.com/mcp");
        parsed.Format.ShouldBe(OutputFormat.Json);
    }

    [Fact]
    public void Parse_OptionMissingValue_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url"
        ])).Message.ShouldContain("requires a value");
    }

    [Fact]
    public void Parse_HeaderWithoutSeparator_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--header", "no-separator"
        ])).Message.ShouldContain("Invalid header");
    }

    [Fact]
    public void Parse_HeaderEmptyName_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--header", "=value"
        ])).Message.ShouldContain("Invalid header");
    }

    [Fact]
    public void Parse_UrlInvalid_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "::not-a-url::"
        ])).Message.ShouldContain("Invalid URL");
    }

    [Fact]
    public void Parse_HeaderOnStdioTarget_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--command", "npx", "--header", "X=Y"
        ])).Message.ShouldContain("--header only applies to URL targets.");
    }

    [Fact]
    public void Parse_TransportOnStdio_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--command", "npx", "--transport", "sse"
        ])).Message.ShouldContain("--transport only applies");
    }

    [Fact]
    public void Parse_ServerWithUrl_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--server", "x"
        ])).Message.ShouldContain("--server only applies to --config");
    }

    [Fact]
    public void Parse_ConfigPlusExtraOptions_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--config", "mcp.json", "--header", "X=Y"
        ])).Message.ShouldContain("only --server, --format, --timeout");
    }

    [Fact]
    public void Parse_ServerOptionRepeats_AreCollected()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--config", "mcp.json", "--server", "a", "--server", "b"
        ]);

        parsed.Target.ServerNames.ShouldBe(new[] { "a", "b" });
    }

    // -------- Auth overrides (Phase A surface: bearer + no-auth + login/logout) ---

    [Fact]
    public void Parse_NoAuthFlags_DefaultsToEmptyOverrides()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp"
        ]);

        parsed.Target.AuthOverrides.ShouldBe(AuthOverrides.Empty);
        parsed.Target.AuthOverrides.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Parse_AuthBearerWithLiteralToken_PopulatesOverrides()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "bearer", "--auth-token", "abc"
        ]);

        parsed.Target.AuthOverrides.Kind.ShouldBe(AuthKind.Bearer);
        parsed.Target.AuthOverrides.Token.ShouldBe("abc");
        parsed.Target.AuthOverrides.NoAuth.ShouldBeFalse();
    }

    [Fact]
    public void Parse_AuthTokenEnvPrefix_IsExpandedAtParseTime()
    {
        const string varName = "MCPLENSE_TEST_TOKEN";
        Environment.SetEnvironmentVariable(varName, "expanded-value");
        try
        {
            var parsed = CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "bearer", "--auth-token", $"env:{varName}"
            ]);

            parsed.Target.AuthOverrides.Token.ShouldBe("expanded-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_AuthTokenEnvPrefixUnset_Throws()
    {
        const string varName = "MCPLENSE_TEST_DEFINITELY_UNSET_XYZ";
        Environment.SetEnvironmentVariable(varName, null);

        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "bearer", "--auth-token", $"env:{varName}"
        ]));

        ex.Message.ShouldContain("--auth-token");
        ex.Message.ShouldContain(varName);
    }

    [Fact]
    public void Parse_AuthTokenExpandsToEmpty_Throws()
    {
        const string varName = "MCPLENSE_TEST_EMPTY";
        Environment.SetEnvironmentVariable(varName, string.Empty);
        try
        {
            var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "bearer", "--auth-token", $"env:{varName}"
            ]));

            ex.Message.ShouldContain("empty value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_AuthOAuth_NoLongerAccepted()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--auth", "oauth"
        ]));

        ex.Message.ShouldContain("oauth");
        ex.Message.ShouldContain("--profile");
    }

    [Fact]
    public void Parse_AuthInteractiveBrowser_NoLongerAccepted()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--auth", "interactive-browser"
        ]));

        ex.Message.ShouldContain("interactive-browser");
        ex.Message.ShouldContain("--profile");
    }

    [Fact]
    public void Parse_AuthUnknownKind_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--auth", "magic"
        ]));

        ex.Message.ShouldContain("Unknown --auth value");
        ex.Message.ShouldContain("magic");
    }

    [Fact]
    public void Parse_NoAuthFlag_IsBoolean_NoValueRequired()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--no-auth"
        ]);

        parsed.Target.AuthOverrides.NoAuth.ShouldBeTrue();
    }

    [Fact]
    public void Parse_NoAuthFlag_DoesNotConsumeNextArg()
    {
        // After --no-auth, --header should still be parseable as a separate flag.
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--no-auth", "--header", "X=Y"
        ]);

        parsed.Target.AuthOverrides.NoAuth.ShouldBeTrue();
        parsed.Target.Headers["X"].ShouldBe("Y");
    }

    [Fact]
    public void Parse_NoAuthDominatesOtherAuthFlags()
    {
        // --no-auth wins; --auth and --auth-token are accepted but cleared.
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "bearer", "--auth-token", "abc", "--no-auth"
        ]);

        var overrides = parsed.Target.AuthOverrides;
        overrides.NoAuth.ShouldBeTrue();
        overrides.Kind.ShouldBeNull();
        overrides.Token.ShouldBeNull();
    }

    [Fact]
    public void Parse_ConfigWithAuthFlags_IsAllowed()
    {
        // Auth flags are explicitly carved out of the --config exclusion check.
        var parsed = CommandLineParser.Parse([
            "inspect", "--config", "mcp.json",
            "--auth", "bearer", "--auth-token", "abc"
        ]);

        parsed.Target.ConfigPaths.ShouldBe(new[] { "mcp.json" });
        parsed.Target.AuthOverrides.Kind.ShouldBe(AuthKind.Bearer);
        parsed.Target.AuthOverrides.Token.ShouldBe("abc");
    }

    [Fact]
    public void Parse_ConfigWithNoAuth_IsAllowed()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--config", "mcp.json", "--no-auth"
        ]);

        parsed.Target.ConfigPaths.ShouldBe(new[] { "mcp.json" });
        parsed.Target.AuthOverrides.NoAuth.ShouldBeTrue();
    }

    [Fact]
    public void Parse_ConfigWithProfileFlags_IsAllowed()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--config", "mcp.json",
            "--profiles", "p.json", "--profile", "agent365"
        ]);

        parsed.Target.ConfigPaths.ShouldBe(new[] { "mcp.json" });
        parsed.Target.ProfilePaths.ShouldBe(new[] { "p.json" });
        parsed.Target.AuthOverrides.Profile.ShouldBe("agent365");
    }

    // -------- Phase C: --login / --logout flags removed; new top-level commands ----

    [Fact]
    public void Parse_LoginFlag_IsRejected()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--login"
        ]));

        // The flag is no longer in the BooleanFlags set; the parser tries to consume the next
        // arg as its value (none exists) and surfaces a "requires a value" error.
        ex.Message.ShouldContain("--login");
    }

    [Fact]
    public void Parse_LogoutFlag_IsRejected()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--logout"
        ]));

        ex.Message.ShouldContain("--logout");
    }

    [Fact]
    public void Parse_LoginFlagWithValue_IsRejected_AsUnknownOption()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--login=true"
        ]));

        ex.Message.ShouldContain("Unknown option '--login'");
    }

    [Fact]
    public void Parse_LoginCommand_NoArgs_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse(["login"]));
        ex.Message.ShouldContain("--all");
        ex.Message.ShouldContain("--profile");
    }

    [Fact]
    public void Parse_LoginCommand_All_PopulatesOverrides()
    {
        var parsed = CommandLineParser.Parse(["login", "--all"]);

        parsed.Command.ShouldBe(AppCommand.Login);
        parsed.Target.AuthOverrides.All.ShouldBeTrue();
    }

    [Fact]
    public void Parse_LoginCommand_Profile_PopulatesOverrides()
    {
        var parsed = CommandLineParser.Parse(["login", "--profile", "agent365"]);

        parsed.Command.ShouldBe(AppCommand.Login);
        parsed.Target.AuthOverrides.Profile.ShouldBe("agent365");
    }

    [Fact]
    public void Parse_LoginCommand_PositionalUrl_PopulatesUrl()
    {
        var parsed = CommandLineParser.Parse(["login", "https://example.com/mcp"]);

        parsed.Command.ShouldBe(AppCommand.Login);
        parsed.Target.Url!.ToString().ShouldStartWith("https://example.com/mcp");
    }

    [Fact]
    public void Parse_LoginCommand_AllAndProfile_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "login", "--all", "--profile", "x"
        ]));

        ex.Message.ShouldContain("exactly one");
    }

    [Fact]
    public void Parse_LoginCommand_AllAndUrl_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "login", "--all", "https://example.com/mcp"
        ]));

        ex.Message.ShouldContain("exactly one");
    }

    [Fact]
    public void Parse_LoginCommand_NonUrlPositional_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "login", "not-a-url"
        ]));

        ex.Message.ShouldContain("absolute http(s) URL");
    }

    [Fact]
    public void Parse_LoginCommand_RejectsNonLoginFlag()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "login", "--all", "--header", "X=Y"
        ]));

        ex.Message.ShouldContain("--header");
        ex.Message.ShouldContain("login");
    }

    [Fact]
    public void Parse_LogoutCommand_All_PopulatesOverrides()
    {
        var parsed = CommandLineParser.Parse(["logout", "--all"]);

        parsed.Command.ShouldBe(AppCommand.Logout);
        parsed.Target.AuthOverrides.All.ShouldBeTrue();
    }

    [Fact]
    public void Parse_LogoutCommand_Profile_PopulatesOverrides()
    {
        var parsed = CommandLineParser.Parse(["logout", "--profile", "agent365"]);

        parsed.Command.ShouldBe(AppCommand.Logout);
        parsed.Target.AuthOverrides.Profile.ShouldBe("agent365");
    }

    [Fact]
    public void Parse_LogoutCommand_NoArgs_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse(["logout"]));
        ex.Message.ShouldContain("--all");
        ex.Message.ShouldContain("--profile");
    }

    [Fact]
    public void Parse_LoginCommand_WithProfilesFile_Loads()
    {
        var parsed = CommandLineParser.Parse([
            "login", "--all", "--profiles", "p1.json", "--profiles", "p2.json"
        ]);

        parsed.Target.ProfilePaths.ShouldBe(new[] { "p1.json", "p2.json" });
    }
}
