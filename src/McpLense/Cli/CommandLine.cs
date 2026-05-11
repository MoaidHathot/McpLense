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
    Prompt
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
    string? ConfigPath,
    IReadOnlyList<string> ServerNames,
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
        "login",
        "logout"
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

        var subject = ParseSubject(command, positionals);
        var arguments = ParseArguments(command, GetSingle(options, "args"));
        var target = ParseTarget(options, stdioTokens);
        var format = ParseFormat(GetSingle(options, "format"));
        var timeout = ParseTimeout(GetSingle(options, "timeout"));
        var progress = ParseProgress(GetSingle(options, "progress"), command);

        // --login/--logout short-circuit through McpExecutor and return an AuthSessionReport.
        // The TUI path casts the executor's payload to InspectReport, so combining 'tui' with
        // --login/--logout would otherwise surface as a confusing internal exception. Reject up
        // front with a clear hint to run the auth action via a non-TUI command first.
        if (command is AppCommand.Tui && (target.AuthOverrides.LoginOnly || target.AuthOverrides.LogoutOnly))
        {
            var flag = target.AuthOverrides.LoginOnly ? "--login" : "--logout";
            throw new UserInputException(
                $"{flag} is not supported with 'tui'. Run 'mcplense inspect ... {flag}' first, then launch 'mcplense tui'.");
        }

        return new ParsedCommand(command, subject, arguments, format, timeout, target, progress);
    }

    private static ParsedCommand HelpCommand() => new(AppCommand.Help, null, null, OutputFormat.Text, TimeSpan.FromSeconds(30), EmptyTarget(), false);

    private static TargetOptions EmptyTarget() => new(null, [], null, null, TransportPreference.Auto, new Dictionary<string, string>(), null, [], null, new Dictionary<string, string>(), AuthOverrides.Empty);

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
            "scope",
            "redirect-uri",
            "token-cache-name",
            "client-id",
            "tenant-id",
            "login",
            "logout"
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

    private static string? ParseSubject(AppCommand command, List<string> positionals)
    {
        return command switch
        {
            AppCommand.Call or AppCommand.Read or AppCommand.Prompt => positionals.Count switch
            {
                0 => throw new UserInputException($"{command.ToString().ToLowerInvariant()} requires a name or URI."),
                1 => positionals[0],
                _ => throw new UserInputException($"{command.ToString().ToLowerInvariant()} accepts a single name or URI.")
            },
            _ => positionals.Count switch
            {
                0 => null,
                _ => throw new UserInputException($"{command.ToString().ToLowerInvariant()} does not accept positional arguments.")
            }
        };
    }

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

    private static TargetOptions ParseTarget(Dictionary<string, List<string>> options, string[] stdioTokens)
    {
        var configPath = GetSingle(options, "config");
        var urlText = GetSingle(options, "url");
        var command = GetSingle(options, "command");
        var commandArgs = GetMany(options, "command-arg").ToList();

        if (stdioTokens.Length > 0)
        {
            if (command is not null || commandArgs.Count > 0)
            {
                throw new UserInputException("Use either '--command/--command-arg' or '-- <command ...>', not both.");
            }

            command = stdioTokens[0];
            commandArgs = stdioTokens.Skip(1).ToList();
        }

        var hasConfig = !string.IsNullOrWhiteSpace(configPath);
        var hasUrl = !string.IsNullOrWhiteSpace(urlText);
        var hasCommand = !string.IsNullOrWhiteSpace(command);
        var directCount = (hasConfig ? 1 : 0) + (hasUrl ? 1 : 0) + (hasCommand ? 1 : 0);

        if (directCount == 0)
        {
            throw new UserInputException("Specify a target with --config, --url, --command, or '-- <command ...>'.");
        }

        if (directCount > 1)
        {
            throw new UserInputException("Specify exactly one target source: config, URL, or stdio command.");
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
            // Config files already carry target definitions; only --server, --format, --timeout
            // and the auth-related overrides may be added on top.
            if (headers.Count > 0 || environment.Count > 0 || workingDirectory is not null || displayName is not null || commandArgs.Count > 0 || command is not null || hasUrl)
            {
                throw new UserInputException(
                    "When using --config, only --server, --format, --timeout, --auth, --auth-token, " +
                    "--scope, --redirect-uri, --token-cache-name, --client-id, --tenant-id, --login, " +
                    "--logout, and --no-auth may be added.");
            }

            return new TargetOptions(configPath, serverNames, null, null, TransportPreference.Auto, new Dictionary<string, string>(), null, [], null, new Dictionary<string, string>(), authOverrides);
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

            return new TargetOptions(null, [], displayName, uri, transport, headers, null, [], null, new Dictionary<string, string>(), authOverrides);
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

        return new TargetOptions(null, [], displayName, null, TransportPreference.Auto, new Dictionary<string, string>(), command, commandArgs, workingDirectory, environment, authOverrides);
    }

    private static AuthOverrides ParseAuthOverrides(Dictionary<string, List<string>> options)
    {
        var noAuthRaw = GetSingle(options, "no-auth");
        var noAuth = string.Equals(noAuthRaw, "true", StringComparison.OrdinalIgnoreCase);

        var loginRaw = GetSingle(options, "login");
        var login = string.Equals(loginRaw, "true", StringComparison.OrdinalIgnoreCase);

        var logoutRaw = GetSingle(options, "logout");
        var logout = string.Equals(logoutRaw, "true", StringComparison.OrdinalIgnoreCase);

        if (login && logout)
        {
            throw new UserInputException("--login and --logout cannot be combined.");
        }

        var authRaw = GetSingle(options, "auth");
        var tokenRaw = GetSingle(options, "auth-token");
        var redirectRaw = GetSingle(options, "redirect-uri");
        var cacheNameRaw = GetSingle(options, "token-cache-name");
        var clientIdRaw = GetSingle(options, "client-id");
        var tenantIdRaw = GetSingle(options, "tenant-id");
        var scopes = GetMany(options, "scope");

        if (noAuth)
        {
            // --no-auth is the dominant flag. We still accept (and ignore) other auth flags so a user
            // toggling --no-auth for a quick debug doesn't have to scrub every other CLI argument.
            // It is, however, incompatible with --login/--logout because those imply OAuth state churn.
            if (login || logout)
            {
                throw new UserInputException("--no-auth cannot be combined with --login or --logout.");
            }

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

        string? redirectUri = null;
        if (redirectRaw is not null)
        {
            redirectUri = expander.Expand(redirectRaw, "--redirect-uri");
            if (string.IsNullOrEmpty(redirectUri))
            {
                throw new UserInputException("--redirect-uri resolved to an empty value.");
            }
        }

        string? cacheName = null;
        if (cacheNameRaw is not null)
        {
            cacheName = expander.Expand(cacheNameRaw, "--token-cache-name");
            if (string.IsNullOrEmpty(cacheName))
            {
                throw new UserInputException("--token-cache-name resolved to an empty value.");
            }
        }

        string? clientId = null;
        if (clientIdRaw is not null)
        {
            clientId = expander.Expand(clientIdRaw, "--client-id");
            if (string.IsNullOrEmpty(clientId))
            {
                throw new UserInputException("--client-id resolved to an empty value.");
            }
        }

        string? tenantId = null;
        if (tenantIdRaw is not null)
        {
            tenantId = expander.Expand(tenantIdRaw, "--tenant-id");
            if (string.IsNullOrEmpty(tenantId))
            {
                throw new UserInputException("--tenant-id resolved to an empty value.");
            }
        }

        IReadOnlyList<string>? expandedScopes = null;
        if (scopes.Count > 0)
        {
            var collected = new List<string>(scopes.Count);
            foreach (var scope in scopes)
            {
                var resolved = expander.Expand(scope, "--scope");
                if (string.IsNullOrEmpty(resolved))
                {
                    throw new UserInputException("--scope resolved to an empty value.");
                }
                collected.Add(resolved);
            }

            expandedScopes = collected;
        }

        return new AuthOverrides(
            Kind: kind,
            Token: token,
            Scopes: expandedScopes,
            RedirectUri: redirectUri,
            CacheName: cacheName,
            ClientId: clientId,
            TenantId: tenantId,
            LoginOnly: login,
            LogoutOnly: logout);
    }

    private static AuthKind ParseAuthKind(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "bearer" => AuthKind.Bearer,
            "oauth" or "oauthdiscovery" => AuthKind.OAuth,
            "interactive-browser" or "interactivebrowser" => AuthKind.InteractiveBrowser,
            _ => throw new UserInputException(
                $"Unknown --auth value '{raw}'. Supported: 'bearer', 'oauth', 'interactive-browser'.")
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
