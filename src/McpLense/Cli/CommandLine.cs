using System.Globalization;
using System.Text.Json.Nodes;

namespace McpLense;

internal enum AppCommand
{
    Help,
    Version,
    Tui,
    Inspect,
    Tools,
    Resources,
    Prompts,
    Call,
    Read,
    Prompt,
    Login,
    Logout
}

internal enum OutputFormat
{
    Text,
    Json,
    Dumpify
}

internal enum TransportPreference
{
    Auto,
    StreamableHttp,
    Sse
}

internal sealed record ParsedCommand(
    AppCommand Command,
    string? Subject,
    JsonObject? Arguments,
    OutputFormat Format,
    TimeSpan Timeout,
    TargetOptions Target,
    bool ProgressEnabled);

internal sealed record TargetOptions(
    IReadOnlyList<string> ConfigPaths,
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<string> ProfilePaths,
    string? DisplayName,
    Uri? Url,
    TransportPreference Transport,
    IReadOnlyDictionary<string, string> Headers,
    string? Command,
    IReadOnlyList<string> CommandArguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    AuthOverrides AuthOverrides);

internal sealed class UserInputException(string message) : Exception(message);

internal static class CommandLineParser
{
    /// <summary>Long options that act as boolean switches and do NOT consume the next argument.</summary>
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "no-auth",
        "try-all",
        "all"
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

        var (subject, urlPositional) = ParseSubjectAndUrl(command, positionals);
        var arguments = ParseArguments(command, GetSingle(options, "args"));
        var target = ParseTarget(options, stdioTokens, urlPositional);
        var format = ParseFormat(GetSingle(options, "format"));
        var timeout = ParseTimeout(GetSingle(options, "timeout"));
        var progress = ParseProgress(GetSingle(options, "progress"), command);

        return new ParsedCommand(command, subject, arguments, format, timeout, target, progress);
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
            "no-auth"
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
    }

    /// <summary>
    /// Splits positional arguments into the (optional) subject for call/read/prompt and the
    /// (optional) URL positional accepted by every other command.
    /// </summary>
    private static (string? Subject, string? UrlPositional) ParseSubjectAndUrl(AppCommand command, List<string> positionals)
    {
        switch (command)
        {
            case AppCommand.Call or AppCommand.Read or AppCommand.Prompt:
                return positionals.Count switch
                {
                    0 => throw new UserInputException($"{command.ToString().ToLowerInvariant()} requires a name or URI."),
                    1 => (positionals[0], null),
                    2 when LooksLikeUrl(positionals[1]) => (positionals[0], positionals[1]),
                    _ => throw new UserInputException(
                        $"{command.ToString().ToLowerInvariant()} accepts a single name or URI, optionally followed by a target URL.")
                };

            default:
                return positionals.Count switch
                {
                    0 => (null, null),
                    1 when LooksLikeUrl(positionals[0]) => (null, positionals[0]),
                    _ => throw new UserInputException(
                        $"{command.ToString().ToLowerInvariant()} accepts at most a single positional URL.")
                };
        }
    }

    private static bool LooksLikeUrl(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

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

    private static TargetOptions ParseTarget(Dictionary<string, List<string>> options, string[] stdioTokens, string? urlPositional)
    {
        var configPaths = GetMany(options, "config");
        var urlText = GetSingle(options, "url") ?? urlPositional;
        var command = GetSingle(options, "command");
        var commandArgs = GetMany(options, "command-arg").ToList();
        var profilePaths = GetMany(options, "profiles");

        if (urlPositional is not null && options.ContainsKey("url"))
        {
            throw new UserInputException("Specify the URL positionally OR via --url, not both.");
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
        var directCount = (hasConfig ? 1 : 0) + (hasUrl ? 1 : 0) + (hasCommand ? 1 : 0);

        if (directCount == 0)
        {
            throw new UserInputException("Specify a target with a positional URL, --config, --url, --command, or '-- <command ...>'.");
        }

        if (directCount > 1)
        {
            throw new UserInputException("Specify exactly one target source: positional URL, --config, --url, --command, or '-- <command ...>'.");
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
            if (headers.Count > 0 || environment.Count > 0 || workingDirectory is not null || displayName is not null || commandArgs.Count > 0 || command is not null || hasUrl)
            {
                throw new UserInputException(
                    "When using --config, only --server, --format, --timeout, --profiles, --profile, " +
                    "--try-all, --auth, --auth-token, and --no-auth may be added.");
            }

            return new TargetOptions(configPaths, serverNames, profilePaths, null, null, TransportPreference.Auto, new Dictionary<string, string>(), null, [], null, new Dictionary<string, string>(), authOverrides);
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

        var authRaw = GetSingle(options, "auth");
        var tokenRaw = GetSingle(options, "auth-token");
        var profileRaw = GetSingle(options, "profile");

        if (tryAll && !string.IsNullOrEmpty(profileRaw))
        {
            throw new UserInputException("--try-all and --profile cannot be combined.");
        }

        if (noAuth)
        {
            // --no-auth dominates. We accept other auth-related flags to make a quick toggle
            // ergonomic, but they're cleared out so behaviour is unambiguous.
            return new AuthOverrides(NoAuth: true);
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
            TryAll: tryAll);
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
