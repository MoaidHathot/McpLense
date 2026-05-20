using System.Globalization;
using System.Text.Json.Nodes;

namespace McpLense;

internal static class CommandLineParser
{
    /// <summary>Long options that act as boolean switches and do NOT consume the next argument.</summary>
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "no-auth",
        "try-all",
        "all",
        "classify-only",
        "check-authorization-servers",
        "quiet",
        "verbose",
        "http-only"
    };

    /// <summary>Long options that can appear multiple times (repeatable).</summary>
    private static readonly HashSet<string> RepeatableOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "config",
        "profiles",
        "server",
        "header",
        "command-arg",
        "env",
        "enable",
        "disable",
        "scan-plugin",
        "targets-from"
    };

    public static ParsedCommand Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return HelpCommand();
        }

        var separatorIndex = Array.IndexOf(args, "--");
        var cliArgs = separatorIndex >= 0 ? args[..separatorIndex] : args;
        var stdioTokens = separatorIndex >= 0 ? args[(separatorIndex + 1)..] : [];

        if (cliArgs.Length == 0)
        {
            throw new UserInputException("Command is required before '--'.");
        }

        var command = ParseCommand(cliArgs[0]);
        if (command is AppCommand.Help)
        {
            return HelpCommand();
        }

        if (command is AppCommand.Version)
        {
            return new ParsedCommand(AppCommand.Version, null, null, OutputFormat.Text, TimeSpan.FromSeconds(30), EmptyTarget(), false);
        }

        if (command is AppCommand.Schema)
        {
            // `schema` accepts one positional kind ("config") and an optional `--output <path>`
            // long option. Reuse the generic parser only for `--output` (and `--format` if the
            // user wants the JSON re-rendered, though schema is always JSON in practice).
            string? kind = null;
            string? output = null;
            for (var idx = 1; idx < cliArgs.Length; idx++)
            {
                var token = cliArgs[idx];
                if (token is "-h" or "--help") return HelpCommand();
                if (token == "--output" || token == "-o")
                {
                    if (idx + 1 >= cliArgs.Length) throw new UserInputException("--output requires a value.");
                    output = cliArgs[++idx];
                    continue;
                }
                if (token.StartsWith("--output=", StringComparison.Ordinal))
                {
                    output = token["--output=".Length..];
                    continue;
                }
                if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    throw new UserInputException($"Unknown option '{token}' for 'schema'.");
                }
                if (kind is not null)
                {
                    throw new UserInputException("'schema' accepts at most one positional argument.");
                }
                kind = token;
            }

            kind ??= "config";
            if (!string.Equals(kind, "config", StringComparison.OrdinalIgnoreCase))
            {
                throw new UserInputException($"Unknown schema kind '{kind}'. Supported: 'config'.");
            }

            var schemaArgs = new JsonObject { ["kind"] = kind };
            if (!string.IsNullOrEmpty(output)) schemaArgs["output"] = output;
            return new ParsedCommand(AppCommand.Schema, kind, schemaArgs, OutputFormat.Json, TimeSpan.FromSeconds(30), EmptyTarget(), false);
        }

        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();

        for (var index = 1; index < cliArgs.Length; index++)
        {
            var token = cliArgs[index];

            if (token is "-h" or "--help")
            {
                return HelpCommand();
            }

            if (token is "-f")
            {
                AddOption(options, "format", RequireValue(token, cliArgs, ref index));
                continue;
            }

            if (token is "-t")
            {
                AddOption(options, "timeout", RequireValue(token, cliArgs, ref index));
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                ParseLongOption(token, cliArgs, options, ref index);
                continue;
            }

            positionals.Add(token);
        }

        ValidateOptions(options, command);

        if (command is AppCommand.Login or AppCommand.Logout)
        {
            return ParseLoginLogout(command, options, positionals);
        }

        var (subject, urlPositional, extraPositionals) = ParseSubjectAndUrl(command, positionals);
        var arguments = ParseArguments(command, GetSingle(options, "args"));

        // --targets-from is a scan-only fan-out source: when present it stands in for the
        // positional URL / @name / --url / --command requirement so the user can hand
        // McpLense the full target list (one URL or @name per line). Validation that the
        // flag is only set for `scan` already happened in ValidateOptions; here we just
        // tell ParseTarget to relax its "specify a target" requirement.
        var hasTargetsFrom = options.ContainsKey("targets-from");
        var target = ParseTarget(options, stdioTokens, urlPositional, allowEmptyTarget: hasTargetsFrom);
        var format = ParseFormat(GetSingle(options, "format"));
        var timeout = ParseTimeout(GetSingle(options, "timeout"));
        var progress = ParseProgress(GetSingle(options, "progress"), command);

        // Scan / observe / fetch-resource / diff specific knobs.
        var baseline = GetSingle(options, "baseline");
        var diffPath = GetSingle(options, "diff");
        var enables = GetMany(options, "enable");
        var disables = GetMany(options, "disable");
        var parallelRaw = GetSingle(options, "parallel-servers");
        int? parallel = null;
        if (parallelRaw is not null)
        {
            if (!int.TryParse(parallelRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) || p < 1)
            {
                throw new UserInputException($"--parallel-servers must be a positive integer, got '{parallelRaw}'.");
            }
            parallel = p;
        }
        var quiet = string.Equals(GetSingle(options, "quiet"), "true", StringComparison.OrdinalIgnoreCase);
        var verbose = string.Equals(GetSingle(options, "verbose"), "true", StringComparison.OrdinalIgnoreCase);
        var scanPlugins = GetMany(options, "scan-plugin");

        var targetsFromPaths = GetMany(options, "targets-from");
        var httpOnly = string.Equals(GetSingle(options, "http-only"), "true", StringComparison.OrdinalIgnoreCase);
        var defaultScopeRaw = GetSingle(options, "default-scope");
        string? defaultScope = null;
        if (!string.IsNullOrEmpty(defaultScopeRaw))
        {
            defaultScope = new EnvironmentExpander().Expand(defaultScopeRaw, "--default-scope");
            if (string.IsNullOrEmpty(defaultScope))
            {
                throw new UserInputException("--default-scope resolved to an empty value.");
            }
        }

        // For 'diff' the two positional arguments are the baseline files - we shove them
        // into Subject + DiffBaselinePath.
        if (command is AppCommand.Diff)
        {
            if (extraPositionals.Count != 1 || subject is null)
            {
                throw new UserInputException("'diff' requires exactly two positional baseline paths: 'mcplense diff <before> <after>'.");
            }

            diffPath ??= extraPositionals[0];
        }

        // Fold the file-list / http-only / default-scope into TargetOptions so the
        // dispatcher / pipeline see them via the same data surface library consumers do.
        target = target with
        {
            TargetsFromPaths = targetsFromPaths.Count > 0 ? targetsFromPaths : null,
            HttpOnly = httpOnly,
            DefaultScope = defaultScope,
            AuthOverrides = target.AuthOverrides with { DefaultScope = defaultScope }
        };

        return new ParsedCommand(
            command, subject, arguments, format, timeout, target, progress,
            BaselinePath: baseline,
            DiffBaselinePath: diffPath,
            CheckEnables: enables.Count > 0 ? enables : null,
            CheckDisables: disables.Count > 0 ? disables : null,
            ParallelServers: parallel,
            Quiet: quiet,
            Verbose: verbose,
            ScanPlugins: scanPlugins.Count > 0 ? scanPlugins : null,
            TargetsFromPaths: targetsFromPaths.Count > 0 ? targetsFromPaths : null,
            HttpOnly: httpOnly,
            DefaultScope: defaultScope);
    }

    /// <summary>
    /// Parses the top-level <c>mcplense login</c> / <c>mcplense logout</c> commands. These
    /// share a much narrower option surface than the read commands: they pick a profile (or all
    /// profiles), optionally take a positional URL for auto-pick, and emit an
    /// <see cref="AuthSessionReport"/>.
    /// </summary>
    private static ParsedCommand ParseLoginLogout(AppCommand command, Dictionary<string, List<string>> options, List<string> positionals)
    {
        var verb = command.ToString().ToLowerInvariant();

        // Reject options that don't apply to login/logout. Allowed:
        // --profiles, --profile, --all, --format, --timeout. Plus the universal -h/--help
        // (which already short-circuits earlier in Parse).
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "profiles", "profile", "all", "format", "timeout"
        };

        foreach (var option in options.Keys)
        {
            if (!allowed.Contains(option))
            {
                throw new UserInputException(
                    $"--{option} is not valid for '{verb}'. Use --all, --profile <name>, or pass a URL positionally.");
            }
        }

        if (positionals.Count > 1)
        {
            throw new UserInputException($"'{verb}' accepts at most one positional URL.");
        }

        string? urlPositional = null;
        if (positionals.Count == 1)
        {
            if (!LooksLikeUrl(positionals[0]))
            {
                throw new UserInputException(
                    $"'{verb}' positional argument must be an absolute http(s) URL.");
            }

            urlPositional = positionals[0];
        }

        var profilePaths = GetMany(options, "profiles");
        var profileRaw = GetSingle(options, "profile");
        var allRaw = GetSingle(options, "all");
        var all = string.Equals(allRaw, "true", StringComparison.OrdinalIgnoreCase);

        var hasProfile = !string.IsNullOrEmpty(profileRaw);
        var hasUrl = urlPositional is not null;

        var selectorCount = (all ? 1 : 0) + (hasProfile ? 1 : 0) + (hasUrl ? 1 : 0);
        if (selectorCount == 0)
        {
            throw new UserInputException(
                $"'{verb}' requires --all, --profile <name>, or a positional URL.");
        }

        if (selectorCount > 1)
        {
            throw new UserInputException(
                $"'{verb}' accepts exactly one of --all, --profile <name>, or a positional URL.");
        }

        var expander = new EnvironmentExpander();
        string? profile = null;
        if (hasProfile)
        {
            profile = expander.Expand(profileRaw, "--profile");
            if (string.IsNullOrEmpty(profile))
            {
                throw new UserInputException("--profile resolved to an empty value.");
            }
        }

        Uri? url = null;
        if (urlPositional is not null && !Uri.TryCreate(urlPositional, UriKind.Absolute, out url))
        {
            throw new UserInputException($"Invalid URL '{urlPositional}'.");
        }

        var format = ParseFormat(GetSingle(options, "format"));
        var timeout = ParseTimeout(GetSingle(options, "timeout"));

        var authOverrides = new AuthOverrides(Profile: profile, All: all);
        var target = new TargetOptions(
            ConfigPaths: [],
            ServerNames: [],
            ProfilePaths: profilePaths,
            DisplayName: null,
            Url: url,
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Command: null,
            CommandArguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            AuthOverrides: authOverrides);

        return new ParsedCommand(command, Subject: null, Arguments: null, format, timeout, target, ProgressEnabled: false);
    }

    private static ParsedCommand HelpCommand() => new(AppCommand.Help, null, null, OutputFormat.Text, TimeSpan.FromSeconds(30), EmptyTarget(), false);

    private static TargetOptions EmptyTarget() => new([], [], [], null, null, TransportPreference.Auto, new Dictionary<string, string>(), null, [], null, new Dictionary<string, string>(), AuthOverrides.Empty);

    private static AppCommand ParseCommand(string value) => value.ToLowerInvariant() switch
    {
        "help" => AppCommand.Help,
        "version" or "--version" or "-v" => AppCommand.Version,
        "tui" => AppCommand.Tui,
        "inspect" => AppCommand.Inspect,
        "tools" => AppCommand.Tools,
        "resources" => AppCommand.Resources,
        "prompts" => AppCommand.Prompts,
        "call" => AppCommand.Call,
        "read" => AppCommand.Read,
        "prompt" => AppCommand.Prompt,
        "login" => AppCommand.Login,
        "logout" => AppCommand.Logout,
        "scan" => AppCommand.Scan,
        "auth-scan" => AppCommand.AuthScan,
        "observe" => AppCommand.Observe,
        "fetch-resource" => AppCommand.FetchResource,
        "diff" => AppCommand.Diff,
        "schema" => AppCommand.Schema,
        _ => throw new UserInputException($"Unknown command '{value}'.")
    };

    private static void ParseLongOption(string token, string[] args, Dictionary<string, List<string>> options, ref int index)
    {
        var separator = token.IndexOf('=');
        var name = separator >= 0 ? token[2..separator] : token[2..];

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new UserInputException("Invalid option name.");
        }

        if (name is "help")
        {
            throw new UserInputException("--help must appear immediately after the command.");
        }

        string value;
        if (separator >= 0)
        {
            value = token[(separator + 1)..];
        }
        else if (BooleanFlags.Contains(name))
        {
            value = "true";
        }
        else
        {
            value = RequireValue(token, args, ref index);
        }

        AddOption(options, name, value);
    }

    private static string RequireValue(string optionName, string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new UserInputException($"Option '{optionName}' requires a value.");
        }

        index++;
        return args[index];
    }

    private static void AddOption(Dictionary<string, List<string>> options, string name, string value)
    {
        if (!options.TryGetValue(name, out var values))
        {
            values = [];
            options[name] = values;
        }

        values.Add(value);
    }

    private static void ValidateOptions(Dictionary<string, List<string>> options, AppCommand command)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "config",
            "server",
            "profiles",
            "profile",
            "try-all",
            "all",
            "name",
            "url",
            "transport",
            "header",
            "command",
            "command-arg",
            "cwd",
            "env",
            "format",
            "timeout",
            "args",
            "progress",
            "auth",
            "auth-token",
            "no-auth",
            "classify-only",
            "check-authorization-servers",
            "baseline",
            "diff",
            "enable",
            "disable",
            "parallel-servers",
            "quiet",
            "verbose",
            "targets-from",
            "http-only",
            "default-scope"
        };

        foreach (var option in options.Keys)
        {
            if (!known.Contains(option))
            {
                throw new UserInputException($"Unknown option '--{option}'.");
            }
        }

        if (command is not (AppCommand.Call or AppCommand.Read or AppCommand.Prompt) && options.ContainsKey("args"))
        {
            throw new UserInputException("--args is only valid for call, read, and prompt.");
        }

        if (command is not AppCommand.Call && options.ContainsKey("progress"))
        {
            throw new UserInputException("--progress is only valid for call.");
        }

        if (command is not (AppCommand.Scan or AppCommand.AuthScan) && options.ContainsKey("classify-only"))
        {
            throw new UserInputException("--classify-only is only valid for 'scan' and 'auth-scan'.");
        }

        if (command is not AppCommand.Scan && options.ContainsKey("check-authorization-servers"))
        {
            throw new UserInputException("--check-authorization-servers is only valid for 'scan'.");
        }

        if (command is not AppCommand.Scan && options.ContainsKey("baseline"))
        {
            throw new UserInputException("--baseline is only valid for 'scan'.");
        }

        if (command is not (AppCommand.Scan or AppCommand.Diff) && options.ContainsKey("diff"))
        {
            throw new UserInputException("--diff is only valid for 'scan' and 'diff'.");
        }

        if (command is not AppCommand.Scan && options.ContainsKey("scan-plugin"))
        {
            throw new UserInputException("--scan-plugin is only valid for 'scan'.");
        }

        if (command is not (AppCommand.Scan or AppCommand.Observe) && (options.ContainsKey("enable") || options.ContainsKey("disable")))
        {
            throw new UserInputException("--enable / --disable are only valid for 'scan' and 'observe'.");
        }

        if (command is not AppCommand.Scan && options.ContainsKey("parallel-servers"))
        {
            throw new UserInputException("--parallel-servers is only valid for 'scan'.");
        }

        if (command is not AppCommand.Scan && options.ContainsKey("targets-from"))
        {
            throw new UserInputException("--targets-from is only valid for 'scan'.");
        }

        if (command is not AppCommand.Scan && options.ContainsKey("http-only"))
        {
            throw new UserInputException("--http-only is only valid for 'scan'.");
        }

        if (command is not (AppCommand.Scan or AppCommand.AuthScan or AppCommand.Inspect or AppCommand.Tools or AppCommand.Resources or AppCommand.Prompts or AppCommand.Call or AppCommand.Read or AppCommand.Prompt or AppCommand.FetchResource or AppCommand.Observe) && options.ContainsKey("default-scope"))
        {
            throw new UserInputException("--default-scope is only valid for scan / inspect / read / call / prompt / fetch-resource / observe / auth-scan / tools / resources / prompts.");
        }

        if (options.ContainsKey("quiet") && options.ContainsKey("verbose"))
        {
            throw new UserInputException("--quiet and --verbose cannot be combined.");
        }
    }

    /// <summary>
    /// Splits positional arguments into the (optional) subject for call/read/prompt/diff and
    /// the (optional) URL positional accepted by every other command. The third tuple field
    /// holds extra positionals after subject (used by <c>diff</c> for the second baseline
    /// path and by <c>fetch-resource</c> for the optional URL). A positional starting with
    /// <c>@</c> is a named-target reference (looked up against <c>targets[].name</c> in the
    /// config file) and is treated exactly like a URL positional - the dispatcher resolves
    /// the actual URL before running the scan.
    /// </summary>
    private static (string? Subject, string? UrlPositional, IReadOnlyList<string> Extras) ParseSubjectAndUrl(AppCommand command, List<string> positionals)
    {
        switch (command)
        {
            case AppCommand.Call or AppCommand.Read or AppCommand.Prompt or AppCommand.FetchResource:
                return positionals.Count switch
                {
                    0 => throw new UserInputException($"{command.ToString().ToLowerInvariant()} requires a name or URI."),
                    1 => (positionals[0], null, Array.Empty<string>()),
                    2 when LooksLikeUrlOrTargetRef(positionals[1]) => (positionals[0], positionals[1], Array.Empty<string>()),
                    _ => throw new UserInputException(
                        $"{command.ToString().ToLowerInvariant()} accepts a single name or URI, optionally followed by a target URL.")
                };

            case AppCommand.Diff:
                // Two positional baseline file paths.
                return positionals.Count switch
                {
                    2 => (positionals[0], null, new[] { positionals[1] }),
                    _ => throw new UserInputException("'diff' requires two positional baseline paths: 'mcplense diff <before> <after>'.")
                };

            default:
                return positionals.Count switch
                {
                    0 => (null, null, Array.Empty<string>()),
                    1 when LooksLikeUrlOrTargetRef(positionals[0]) => (null, positionals[0], Array.Empty<string>()),
                    _ => throw new UserInputException(
                        $"{command.ToString().ToLowerInvariant()} accepts at most a single positional URL.")
                };
        }
    }

    private static bool LooksLikeUrl(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeNamedReference(string value)
        => value.Length > 1 && value[0] == '@';

    private static bool LooksLikeUrlOrTargetRef(string value)
        => LooksLikeUrl(value) || LooksLikeNamedReference(value);

    private static JsonObject? ParseArguments(AppCommand command, string? value)
    {
        if (value is null)
        {
            return command is AppCommand.Call or AppCommand.Prompt ? new JsonObject() : null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(value);
        }
        catch (Exception ex)
        {
            throw new UserInputException($"Invalid JSON for --args: {ex.Message}");
        }

        if (node is not JsonObject obj)
        {
            throw new UserInputException("--args must be a JSON object.");
        }

        return obj;
    }

    private static TargetOptions ParseTarget(Dictionary<string, List<string>> options, string[] stdioTokens, string? urlPositional, bool allowEmptyTarget = false)
    {
        var configPaths = GetMany(options, "config");
        // A positional that starts with '@' is a named-target reference (looked up against
        // the config file's `targets[]` block by the dispatcher). It is treated as a URL
        // alternative - exactly one target source is allowed.
        string? namedReference = null;
        if (urlPositional is { Length: > 0 } && urlPositional[0] == '@')
        {
            namedReference = urlPositional[1..];
            urlPositional = null;
        }

        var urlText = GetSingle(options, "url") ?? urlPositional;
        var command = GetSingle(options, "command");
        var commandArgs = GetMany(options, "command-arg").ToList();
        var profilePaths = GetMany(options, "profiles");

        if (urlPositional is not null && options.ContainsKey("url"))
        {
            throw new UserInputException("Specify the URL positionally OR via --url, not both.");
        }

        if (namedReference is not null && options.ContainsKey("url"))
        {
            throw new UserInputException("Specify a named target reference (@name) OR --url, not both.");
        }

        if (stdioTokens.Length > 0)
        {
            if (command is not null || commandArgs.Count > 0)
            {
                throw new UserInputException("Use either '--command/--command-arg' or '-- <command ...>', not both.");
            }

            command = stdioTokens[0];
            commandArgs = stdioTokens.Skip(1).ToList();
        }

        var hasConfig = configPaths.Count > 0;
        var hasUrl = !string.IsNullOrWhiteSpace(urlText);
        var hasCommand = !string.IsNullOrWhiteSpace(command);
        var hasNamedRef = !string.IsNullOrEmpty(namedReference);
        var directCount = (hasConfig ? 1 : 0) + (hasUrl ? 1 : 0) + (hasCommand ? 1 : 0) + (hasNamedRef ? 1 : 0);

        if (directCount == 0)
        {
            if (allowEmptyTarget)
            {
                // --targets-from path: no positional target needed; the dispatcher will read
                // URLs from the file(s).
                var serverNamesEmpty = GetMany(options, "server");
                if (serverNamesEmpty.Count > 0)
                {
                    throw new UserInputException("--server only applies to --config.");
                }
                return new TargetOptions(
                    ConfigPaths: [],
                    ServerNames: [],
                    ProfilePaths: profilePaths,
                    DisplayName: null,
                    Url: null,
                    Transport: TransportPreference.Auto,
                    Headers: new Dictionary<string, string>(),
                    Command: null,
                    CommandArguments: [],
                    WorkingDirectory: null,
                    Environment: new Dictionary<string, string>(),
                    AuthOverrides: ParseAuthOverrides(options));
            }

            throw new UserInputException("Specify a target with a positional URL, @<target-name>, --config, --url, --command, or '-- <command ...>'.");
        }

        if (directCount > 1)
        {
            throw new UserInputException("Specify exactly one target source: positional URL, @<target-name>, --config, --url, --command, or '-- <command ...>'.");
        }

        var serverNames = GetMany(options, "server");
        var headers = ParsePairs(GetMany(options, "header"), "header");
        var environment = ParsePairs(GetMany(options, "env"), "env");
        var workingDirectory = GetSingle(options, "cwd");
        var displayName = GetSingle(options, "name");
        var transport = ParseTransport(GetSingle(options, "transport"));
        var authOverrides = ParseAuthOverrides(options);

        if (hasConfig)
        {
            // Config files are stdio-only; only --server, --format, --timeout, the auth-related
            // overrides, and --profiles may be added on top.
            if (headers.Count > 0 || environment.Count > 0 || workingDirectory is not null || displayName is not null || commandArgs.Count > 0 || command is not null || hasUrl || hasNamedRef)
            {
                throw new UserInputException(
                    "When using --config, only --server, --format, --timeout, --profiles, --profile, " +
                    "--try-all, --auth, --auth-token, and --no-auth may be added.");
            }

            return new TargetOptions(configPaths, serverNames, profilePaths, null, null, TransportPreference.Auto, new Dictionary<string, string>(), null, [], null, new Dictionary<string, string>(), authOverrides);
        }

        if (hasNamedRef)
        {
            if (serverNames.Count > 0)
            {
                throw new UserInputException("--server only applies to --config.");
            }

            if (workingDirectory is not null || environment.Count > 0)
            {
                throw new UserInputException("--cwd and --env only apply to stdio targets.");
            }

            // The named reference will be resolved by the dispatcher after loading the config
            // file. Headers + transport + display-name remain valid CLI knobs and overlay
            // on top of any per-target defaults the named entry supplies.
            return new TargetOptions(
                ConfigPaths: [],
                ServerNames: [],
                ProfilePaths: profilePaths,
                DisplayName: displayName,
                Url: null,
                Transport: transport,
                Headers: headers,
                Command: null,
                CommandArguments: [],
                WorkingDirectory: null,
                Environment: new Dictionary<string, string>(),
                AuthOverrides: authOverrides,
                NamedReference: namedReference);
        }

        if (hasUrl)
        {
            if (serverNames.Count > 0)
            {
                throw new UserInputException("--server only applies to --config.");
            }

            if (workingDirectory is not null || environment.Count > 0)
            {
                throw new UserInputException("--cwd and --env only apply to stdio targets.");
            }

            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
            {
                throw new UserInputException($"Invalid URL '{urlText}'.");
            }

            return new TargetOptions([], [], profilePaths, displayName, uri, transport, headers, null, [], null, new Dictionary<string, string>(), authOverrides);
        }

        if (headers.Count > 0)
        {
            throw new UserInputException("--header only applies to URL targets.");
        }

        if (serverNames.Count > 0)
        {
            throw new UserInputException("--server only applies to --config.");
        }

        if (GetSingle(options, "transport") is not null)
        {
            throw new UserInputException("--transport only applies to URL targets.");
        }

        if (profilePaths.Count > 0 && !hasUrl && !hasConfig)
        {
            throw new UserInputException("--profiles is only meaningful when targeting an HTTP MCP via URL or --config.");
        }

        return new TargetOptions([], [], profilePaths, displayName, null, TransportPreference.Auto, new Dictionary<string, string>(), command, commandArgs, workingDirectory, environment, authOverrides);
    }

    private static AuthOverrides ParseAuthOverrides(Dictionary<string, List<string>> options)
    {
        var noAuthRaw = GetSingle(options, "no-auth");
        var noAuth = string.Equals(noAuthRaw, "true", StringComparison.OrdinalIgnoreCase);

        var tryAllRaw = GetSingle(options, "try-all");
        var tryAll = string.Equals(tryAllRaw, "true", StringComparison.OrdinalIgnoreCase);

        var classifyOnlyRaw = GetSingle(options, "classify-only");
        var classifyOnly = string.Equals(classifyOnlyRaw, "true", StringComparison.OrdinalIgnoreCase);

        var checkAsRaw = GetSingle(options, "check-authorization-servers");
        var checkAs = string.Equals(checkAsRaw, "true", StringComparison.OrdinalIgnoreCase);

        var authRaw = GetSingle(options, "auth");
        var tokenRaw = GetSingle(options, "auth-token");
        var profileRaw = GetSingle(options, "profile");

        if (tryAll && !string.IsNullOrEmpty(profileRaw))
        {
            throw new UserInputException("--try-all and --profile cannot be combined.");
        }

        if (classifyOnly && !string.IsNullOrEmpty(profileRaw))
        {
            // --classify-only is "skip profile attempts", so pairing it with an explicit
            // single-profile pick is contradictory. Catch this here so a stray flag combo
            // doesn't silently degrade to "ignore --profile".
            throw new UserInputException("--classify-only and --profile cannot be combined.");
        }

        if (noAuth)
        {
            // --no-auth dominates. We accept other auth-related flags to make a quick toggle
            // ergonomic, but they're cleared out so behaviour is unambiguous.
            return new AuthOverrides(NoAuth: true, ClassifyOnly: classifyOnly, CheckAuthorizationServers: checkAs);
        }

        AuthKind? kind = null;
        if (authRaw is not null)
        {
            kind = ParseAuthKind(authRaw);
        }

        var expander = new EnvironmentExpander();

        string? token = null;
        if (tokenRaw is not null)
        {
            token = expander.Expand(tokenRaw, "--auth-token");
            if (string.IsNullOrEmpty(token))
            {
                throw new UserInputException("--auth-token resolved to an empty value.");
            }
        }

        string? profile = null;
        if (profileRaw is not null)
        {
            profile = expander.Expand(profileRaw, "--profile");
            if (string.IsNullOrEmpty(profile))
            {
                throw new UserInputException("--profile resolved to an empty value.");
            }
        }

        return new AuthOverrides(
            Kind: kind,
            Token: token,
            Profile: profile,
            TryAll: tryAll,
            ClassifyOnly: classifyOnly,
            CheckAuthorizationServers: checkAs);
    }

    private static AuthKind ParseAuthKind(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "bearer" => AuthKind.Bearer,
            _ => throw new UserInputException(
                $"Unknown --auth value '{raw}'. The CLI ad-hoc form only supports 'bearer'; " +
                "use a profile (via --profile <name> + --profiles <path>) for OAuth or interactive-browser auth.")
        };
    }

    private static IReadOnlyDictionary<string, string> ParsePairs(IReadOnlyList<string> values, string label)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            var separator = value.IndexOf('=');
            if (separator < 0)
            {
                separator = value.IndexOf(':');
            }

            if (separator <= 0)
            {
                throw new UserInputException($"Invalid {label} '{value}'. Expected name=value.");
            }

            var name = value[..separator].Trim();
            var pairValue = value[(separator + 1)..].Trim();

            if (name.Length == 0)
            {
                throw new UserInputException($"Invalid {label} '{value}'. Name cannot be empty.");
            }

            map[name] = pairValue;
        }

        return map;
    }

    private static OutputFormat ParseFormat(string? value) => value?.ToLowerInvariant() switch
    {
        null or "text" => OutputFormat.Text,
        "json" => OutputFormat.Json,
        "jsonl" or "ndjson" => OutputFormat.Jsonl,
        "dump" or "dumpify" => OutputFormat.Dumpify,
        _ => throw new UserInputException($"Unknown format '{value}'.")
    };

    private static TransportPreference ParseTransport(string? value) => value?.ToLowerInvariant() switch
    {
        null or "auto" => TransportPreference.Auto,
        "streamable-http" or "streamablehttp" or "http" => TransportPreference.StreamableHttp,
        "sse" => TransportPreference.Sse,
        _ => throw new UserInputException($"Unknown transport '{value}'.")
    };

    private static TimeSpan ParseTimeout(string? value)
    {
        if (value is null)
        {
            return TimeSpan.FromSeconds(30);
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            throw new UserInputException($"Invalid timeout '{value}'. Use a positive number of seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static bool ParseProgress(string? value, AppCommand command)
    {
        if (command is not AppCommand.Call)
        {
            return false;
        }

        return value?.ToLowerInvariant() switch
        {
            null => true,
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => throw new UserInputException($"Unknown progress value '{value}'. Use true or false.")
        };
    }

    private static string? GetSingle(Dictionary<string, List<string>> options, string name)
    {
        if (!options.TryGetValue(name, out var values))
        {
            return null;
        }

        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => throw new UserInputException($"Option '--{name}' can only be specified once.")
        };
    }

    private static IReadOnlyList<string> GetMany(Dictionary<string, List<string>> options, string name)
        => options.TryGetValue(name, out var values) ? values : [];
}
