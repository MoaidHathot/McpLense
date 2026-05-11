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

        parsed.Target.ConfigPath.ShouldBe("mcp.json");
        parsed.Target.Url.ShouldBeNull();
        parsed.Target.Command.ShouldBeNull();
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
    public void Parse_InspectRejectsPositionals()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "extra", "--url", "https://example.com/mcp"
        ])).Message.ShouldContain("does not accept positional");
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
    public void Parse_AuthUnknownKind_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "magic"
        ]));

        ex.Message.ShouldContain("Unknown --auth value");
        ex.Message.ShouldContain("magic");
    }

    [Theory]
    [InlineData("oauth")]
    [InlineData("OAuthDiscovery")]
    public void Parse_AuthOAuth_AcceptedAtParseTime(string value)
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", value
        ]);

        parsed.Target.AuthOverrides.Kind.ShouldBe(AuthKind.OAuth);
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

        parsed.Target.ConfigPath.ShouldBe("mcp.json");
        parsed.Target.AuthOverrides.Kind.ShouldBe(AuthKind.Bearer);
        parsed.Target.AuthOverrides.Token.ShouldBe("abc");
    }

    [Fact]
    public void Parse_ConfigWithNoAuth_IsAllowed()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--config", "mcp.json", "--no-auth"
        ]);

        parsed.Target.ConfigPath.ShouldBe("mcp.json");
        parsed.Target.AuthOverrides.NoAuth.ShouldBeTrue();
    }

    // -------- OAuth-specific overrides (Slice B) ----------------------------------

    [Fact]
    public void Parse_Scope_Single_PopulatesScopes()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "oauth", "--scope", "mcp.read"
        ]);

        parsed.Target.AuthOverrides.Scopes.ShouldNotBeNull();
        parsed.Target.AuthOverrides.Scopes!.ShouldBe(new[] { "mcp.read" });
    }

    [Fact]
    public void Parse_Scope_Repeated_CollectsAllValuesInOrder()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "oauth",
            "--scope", "mcp.read",
            "--scope", "mcp.write",
            "--scope", "offline_access"
        ]);

        parsed.Target.AuthOverrides.Scopes!.ShouldBe(new[] { "mcp.read", "mcp.write", "offline_access" });
    }

    [Fact]
    public void Parse_Scope_EnvPrefix_IsExpandedAtParseTime()
    {
        const string varName = "MCPLENSE_TEST_SCOPE";
        Environment.SetEnvironmentVariable(varName, "mcp.admin");
        try
        {
            var parsed = CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "oauth", "--scope", $"env:{varName}"
            ]);

            parsed.Target.AuthOverrides.Scopes!.Single().ShouldBe("mcp.admin");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_Scope_EnvUnset_Throws()
    {
        const string varName = "MCPLENSE_TEST_SCOPE_UNSET_XYZ";
        Environment.SetEnvironmentVariable(varName, null);

        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "oauth", "--scope", $"env:{varName}"
        ]));

        ex.Message.ShouldContain("--scope");
        ex.Message.ShouldContain(varName);
    }

    [Fact]
    public void Parse_Scope_EnvExpandsToEmpty_Throws()
    {
        const string varName = "MCPLENSE_TEST_SCOPE_EMPTY";
        Environment.SetEnvironmentVariable(varName, string.Empty);
        try
        {
            var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "oauth", "--scope", $"env:{varName}"
            ]));

            ex.Message.ShouldContain("empty value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_RedirectUri_Literal_PopulatesOverride()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "oauth", "--redirect-uri", "http://127.0.0.1:5050/callback"
        ]);

        parsed.Target.AuthOverrides.RedirectUri.ShouldBe("http://127.0.0.1:5050/callback");
    }

    [Fact]
    public void Parse_RedirectUri_EnvPrefix_IsExpanded()
    {
        const string varName = "MCPLENSE_TEST_REDIRECT";
        Environment.SetEnvironmentVariable(varName, "http://127.0.0.1:6060/cb");
        try
        {
            var parsed = CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "oauth", "--redirect-uri", $"env:{varName}"
            ]);

            parsed.Target.AuthOverrides.RedirectUri.ShouldBe("http://127.0.0.1:6060/cb");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_RedirectUri_ExpandsToEmpty_Throws()
    {
        const string varName = "MCPLENSE_TEST_REDIRECT_EMPTY";
        Environment.SetEnvironmentVariable(varName, string.Empty);
        try
        {
            var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "oauth", "--redirect-uri", $"env:{varName}"
            ]));

            ex.Message.ShouldContain("--redirect-uri");
            ex.Message.ShouldContain("empty value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_TokenCacheName_Literal_PopulatesOverride()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "oauth", "--token-cache-name", "my-server"
        ]);

        parsed.Target.AuthOverrides.CacheName.ShouldBe("my-server");
    }

    [Fact]
    public void Parse_TokenCacheName_EnvPrefix_IsExpanded()
    {
        const string varName = "MCPLENSE_TEST_CACHE_NAME";
        Environment.SetEnvironmentVariable(varName, "alias-cache");
        try
        {
            var parsed = CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "oauth", "--token-cache-name", $"env:{varName}"
            ]);

            parsed.Target.AuthOverrides.CacheName.ShouldBe("alias-cache");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_TokenCacheName_ExpandsToEmpty_Throws()
    {
        const string varName = "MCPLENSE_TEST_CACHE_NAME_EMPTY";
        Environment.SetEnvironmentVariable(varName, string.Empty);
        try
        {
            var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "oauth", "--token-cache-name", $"env:{varName}"
            ]));

            ex.Message.ShouldContain("--token-cache-name");
            ex.Message.ShouldContain("empty value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_Login_IsBoolean_NoValueRequired()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "oauth", "--login"
        ]);

        parsed.Target.AuthOverrides.LoginOnly.ShouldBeTrue();
        parsed.Target.AuthOverrides.LogoutOnly.ShouldBeFalse();
    }

    [Fact]
    public void Parse_Login_DoesNotConsumeNextArg()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "oauth", "--login", "--scope", "mcp.read"
        ]);

        parsed.Target.AuthOverrides.LoginOnly.ShouldBeTrue();
        parsed.Target.AuthOverrides.Scopes!.ShouldBe(new[] { "mcp.read" });
    }

    [Fact]
    public void Parse_Logout_IsBoolean_NoValueRequired()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "oauth", "--logout"
        ]);

        parsed.Target.AuthOverrides.LogoutOnly.ShouldBeTrue();
        parsed.Target.AuthOverrides.LoginOnly.ShouldBeFalse();
    }

    [Fact]
    public void Parse_LoginAndLogoutTogether_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "oauth", "--login", "--logout"
        ]));

        ex.Message.ShouldContain("--login");
        ex.Message.ShouldContain("--logout");
    }

    [Fact]
    public void Parse_NoAuthWithLogin_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--no-auth", "--login"
        ]));

        ex.Message.ShouldContain("--no-auth");
        ex.Message.ShouldContain("--login");
    }

    [Fact]
    public void Parse_NoAuthWithLogout_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--no-auth", "--logout"
        ]));

        ex.Message.ShouldContain("--no-auth");
        ex.Message.ShouldContain("--logout");
    }

    [Fact]
    public void Parse_TuiWithLogin_Throws()
    {
        // TuiApp casts McpExecutor's payload to InspectReport. --login short-circuits to an
        // AuthSessionReport, which would trip an internal exception. Reject up front.
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "tui", "--url", "https://example.com/mcp",
            "--auth", "oauth", "--login"
        ]));

        ex.Message.ShouldContain("--login");
        ex.Message.ShouldContain("tui");
        ex.Message.ShouldContain("inspect");
    }

    [Fact]
    public void Parse_TuiWithLogout_Throws()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "tui", "--url", "https://example.com/mcp",
            "--auth", "oauth", "--logout"
        ]));

        ex.Message.ShouldContain("--logout");
        ex.Message.ShouldContain("tui");
        ex.Message.ShouldContain("inspect");
    }

    [Fact]
    public void Parse_ConfigWithOAuthFlags_IsAllowed()
    {
        // The full set of OAuth-related overrides should be permitted alongside --config.
        var parsed = CommandLineParser.Parse([
            "inspect", "--config", "mcp.json",
            "--auth", "oauth",
            "--scope", "mcp.read",
            "--redirect-uri", "http://127.0.0.1:5050/callback",
            "--token-cache-name", "my-cache"
        ]);

        var overrides = parsed.Target.AuthOverrides;
        parsed.Target.ConfigPath.ShouldBe("mcp.json");
        overrides.Kind.ShouldBe(AuthKind.OAuth);
        overrides.Scopes!.ShouldBe(new[] { "mcp.read" });
        overrides.RedirectUri.ShouldBe("http://127.0.0.1:5050/callback");
        overrides.CacheName.ShouldBe("my-cache");
    }

    [Fact]
    public void Parse_ConfigWithLoginFlag_IsAllowed()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--config", "mcp.json", "--login"
        ]);

        parsed.Target.ConfigPath.ShouldBe("mcp.json");
        parsed.Target.AuthOverrides.LoginOnly.ShouldBeTrue();
    }

    [Fact]
    public void Parse_ConfigWithLogoutFlag_IsAllowed()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--config", "mcp.json", "--logout"
        ]);

        parsed.Target.ConfigPath.ShouldBe("mcp.json");
        parsed.Target.AuthOverrides.LogoutOnly.ShouldBeTrue();
    }

    [Fact]
    public void Parse_LoginWithoutAuthFlag_StillSetsLoginOnly()
    {
        // --login should be parseable on its own; the per-server auth resolution decides whether
        // OAuth is actually configured, surfacing the error at runtime if not.
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp", "--login"
        ]);

        parsed.Target.AuthOverrides.LoginOnly.ShouldBeTrue();
        parsed.Target.AuthOverrides.Kind.ShouldBeNull();
    }

    [Fact]
    public void Parse_ScopeSpecifiedTwiceWithEqualsForm_BothCollected()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "oauth",
            "--scope=mcp.read",
            "--scope=mcp.write"
        ]);

        parsed.Target.AuthOverrides.Scopes!.ShouldBe(new[] { "mcp.read", "mcp.write" });
    }

    // -------- InteractiveBrowser (M365 / Entra ID) --------------------------------

    [Theory]
    [InlineData("interactive-browser")]
    [InlineData("interactivebrowser")]
    [InlineData("INTERACTIVE-BROWSER")]
    public void Parse_AuthInteractiveBrowser_RecognisesAlias(string value)
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", value,
            "--client-id", "abc",
            "--scope", "api://x/.default"
        ]);

        parsed.Target.AuthOverrides.Kind.ShouldBe(AuthKind.InteractiveBrowser);
    }

    [Fact]
    public void Parse_AuthErrorMessageMentionsInteractiveBrowser()
    {
        var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "magic"
        ]));

        ex.Message.ShouldContain("interactive-browser");
    }

    [Fact]
    public void Parse_ClientId_Literal_PopulatesOverride()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "interactive-browser",
            "--client-id", "aebc6443-996d-45c2-90f0-388ff96faa56",
            "--scope", "api://x/.default"
        ]);

        parsed.Target.AuthOverrides.ClientId.ShouldBe("aebc6443-996d-45c2-90f0-388ff96faa56");
    }

    [Fact]
    public void Parse_ClientId_EnvPrefix_IsExpanded()
    {
        const string varName = "MCPLENSE_TEST_CLIENT_ID";
        Environment.SetEnvironmentVariable(varName, "expanded-client-id");
        try
        {
            var parsed = CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "interactive-browser",
                "--client-id", $"env:{varName}",
                "--scope", "api://x/.default"
            ]);

            parsed.Target.AuthOverrides.ClientId.ShouldBe("expanded-client-id");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_ClientId_ExpandsToEmpty_Throws()
    {
        const string varName = "MCPLENSE_TEST_CLIENT_ID_EMPTY";
        Environment.SetEnvironmentVariable(varName, string.Empty);
        try
        {
            var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "interactive-browser",
                "--client-id", $"env:{varName}",
                "--scope", "api://x/.default"
            ]));

            ex.Message.ShouldContain("--client-id");
            ex.Message.ShouldContain("empty value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_TenantId_Literal_PopulatesOverride()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "interactive-browser",
            "--client-id", "abc",
            "--tenant-id", "common",
            "--scope", "api://x/.default"
        ]);

        parsed.Target.AuthOverrides.TenantId.ShouldBe("common");
    }

    [Fact]
    public void Parse_TenantId_EnvPrefix_IsExpanded()
    {
        const string varName = "MCPLENSE_TEST_TENANT_ID";
        Environment.SetEnvironmentVariable(varName, "contoso.onmicrosoft.com");
        try
        {
            var parsed = CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "interactive-browser",
                "--client-id", "abc",
                "--tenant-id", $"env:{varName}",
                "--scope", "api://x/.default"
            ]);

            parsed.Target.AuthOverrides.TenantId.ShouldBe("contoso.onmicrosoft.com");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_TenantId_ExpandsToEmpty_Throws()
    {
        const string varName = "MCPLENSE_TEST_TENANT_ID_EMPTY";
        Environment.SetEnvironmentVariable(varName, string.Empty);
        try
        {
            var ex = Should.Throw<UserInputException>(() => CommandLineParser.Parse([
                "inspect", "--url", "https://example.com/mcp",
                "--auth", "interactive-browser",
                "--client-id", "abc",
                "--tenant-id", $"env:{varName}",
                "--scope", "api://x/.default"
            ]));

            ex.Message.ShouldContain("--tenant-id");
            ex.Message.ShouldContain("empty value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Parse_AuthInteractiveBrowserWithoutClientId_FailsAtMerge()
    {
        // Parser accepts the partial input; the missing clientId surfaces only when TargetResolver
        // merges the overrides into a ResolvedAuth. This split keeps the parser purely syntactic.
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "interactive-browser",
            "--scope", "api://x/.default"
        ]);

        parsed.Target.AuthOverrides.Kind.ShouldBe(AuthKind.InteractiveBrowser);
        parsed.Target.AuthOverrides.ClientId.ShouldBeNull();
    }

    [Fact]
    public void Parse_AllAuthFlagsTogether_IsAllowed()
    {
        // Spot-check that the full set of overrides survives parsing intact (kind, scopes,
        // redirect-uri, token-cache-name, client-id, tenant-id, login).
        var parsed = CommandLineParser.Parse([
            "inspect", "--url", "https://example.com/mcp",
            "--auth", "interactive-browser",
            "--client-id", "abc",
            "--tenant-id", "common",
            "--scope", "api://x/.default",
            "--redirect-uri", "http://localhost",
            "--token-cache-name", "mcp-proxy",
            "--login"
        ]);

        var overrides = parsed.Target.AuthOverrides;
        overrides.Kind.ShouldBe(AuthKind.InteractiveBrowser);
        overrides.ClientId.ShouldBe("abc");
        overrides.TenantId.ShouldBe("common");
        overrides.Scopes!.ShouldBe(new[] { "api://x/.default" });
        overrides.RedirectUri.ShouldBe("http://localhost");
        overrides.CacheName.ShouldBe("mcp-proxy");
        overrides.LoginOnly.ShouldBeTrue();
    }

    [Fact]
    public void Parse_ConfigWithClientIdAndTenantId_IsAllowed()
    {
        var parsed = CommandLineParser.Parse([
            "inspect", "--config", "mcp.json",
            "--client-id", "abc",
            "--tenant-id", "common"
        ]);

        parsed.Target.ConfigPath.ShouldBe("mcp.json");
        parsed.Target.AuthOverrides.ClientId.ShouldBe("abc");
        parsed.Target.AuthOverrides.TenantId.ShouldBe("common");
    }
}
