using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace McpLense;

/// <summary>
/// Turns an MCP tool input-schema (JSON Schema), a prompt's declared arguments, or a
/// resource URI-template into an interactive Spectre.Console prompt flow and collects the
/// answers into a <see cref="JsonObject"/> ready to hand to the executor.
/// </summary>
/// <remarks>
/// The schema-parsing, type coercion, URI-template variable extraction and equivalent-command
/// rendering are deliberately split out as pure static methods so they can be unit-tested
/// without a console. Only the <c>Elicit*</c> entry points touch <see cref="IAnsiConsole"/>.
///
/// Defaults are first-class: when a property declares a <c>default</c> the prompt pre-fills it
/// and pressing Enter accepts it - the user is never forced to retype a value that already has
/// a sensible default, but may always override it.
/// </remarks>
internal static class ArgumentElicitor
{
    /// <summary>One elicit-able property distilled from a JSON-Schema <c>properties</c> entry.</summary>
    internal sealed record SchemaProperty(
        string Name,
        string? Type,
        bool Required,
        bool HasDefault,
        JsonNode? Default,
        IReadOnlyList<JsonNode>? EnumValues,
        string? Description);

    private const string SkipChoice = "(skip)";

    // ---------------- Console entry points ----------------

    /// <summary>Prompts for every property in a tool's <c>inputSchema</c>.</summary>
    public static JsonObject ElicitToolArguments(IAnsiConsole console, JsonNode? inputSchema)
    {
        var properties = PlanProperties(inputSchema);
        var result = new JsonObject();
        if (properties.Count == 0)
        {
            console.MarkupLine("[grey]This tool takes no arguments.[/]");
            return result;
        }

        console.MarkupLine("[grey]Enter arguments. Press Enter to accept a shown default; leave an optional value blank to skip.[/]");
        foreach (var property in properties)
        {
            var (include, value) = PromptForProperty(console, property);
            if (include)
            {
                result[property.Name] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Prompts for each declared prompt argument (prompt arguments are always strings). When a
    /// <paramref name="completions"/> source is supplied and the server offers suggestions, they
    /// are shown as a pick-list with an "enter a custom value" escape.
    /// </summary>
    public static async Task<JsonObject> ElicitPromptArgumentsAsync(IAnsiConsole console, IReadOnlyList<PromptArgumentInfo> arguments, ICompletionSource? completions = null)
    {
        var result = new JsonObject();
        if (arguments.Count == 0)
        {
            console.MarkupLine("[grey]This prompt takes no arguments.[/]");
            return result;
        }

        console.MarkupLine("[grey]Enter prompt arguments. Leave an optional value blank to skip.[/]");
        foreach (var argument in arguments)
        {
            if (string.IsNullOrEmpty(argument.Name))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(argument.Description))
            {
                console.MarkupLine($"[grey]{Markup.Escape(argument.Name)}: {Markup.Escape(argument.Description!)}[/]");
            }

            var marker = argument.Required ? " [red]*[/]" : string.Empty;
            var label = $"[green]{Markup.Escape(argument.Name)}[/]{marker}";
            var value = await PromptStringAsync(console, label, argument.Required, completions, argument.Name).ConfigureAwait(false);
            if (value is null)
            {
                continue;
            }

            result[argument.Name] = JsonValue.Create(value);
        }

        return result;
    }

    /// <summary>
    /// Prompts for each <c>{variable}</c> in a resource URI-template, offering server completions
    /// when available.
    /// </summary>
    public static async Task<JsonObject> ElicitTemplateVariablesAsync(IAnsiConsole console, string uriTemplate, ICompletionSource? completions = null)
    {
        var variables = ExtractTemplateVariables(uriTemplate);
        var result = new JsonObject();
        if (variables.Count == 0)
        {
            return result;
        }

        console.MarkupLine("[grey]Fill in the URI-template variables:[/]");
        foreach (var variable in variables)
        {
            var label = $"[green]{Markup.Escape(variable)}[/]";
            var value = await PromptStringAsync(console, label, required: true, completions, variable).ConfigureAwait(false);
            result[variable] = JsonValue.Create(value ?? string.Empty);
        }

        return result;
    }

    /// <summary>
    /// Prompts for one string value. When the completion source yields suggestions, shows them as a
    /// selection (plus an "enter a custom value" escape, and "(skip)" for optional args); otherwise
    /// falls back to free-text entry. Returns null when an optional value was left blank/skipped.
    /// </summary>
    private static async Task<string?> PromptStringAsync(IAnsiConsole console, string label, bool required, ICompletionSource? completions, string argumentName)
    {
        if (completions is not null)
        {
            IReadOnlyList<string> suggestions;
            try
            {
                suggestions = await completions.CompleteAsync(argumentName, string.Empty, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                suggestions = [];
            }

            if (suggestions.Count > 0)
            {
                const string custom = "(enter a custom value)";
                var choices = new List<string>(suggestions) { custom };
                if (!required)
                {
                    choices.Add(SkipChoice);
                }

                var selection = console.Prompt(new SelectionPrompt<string>().UseConverter(Markup.Escape).Title(label).PageSize(12).AddChoices(choices));
                if (selection == SkipChoice)
                {
                    return null;
                }

                if (selection != custom)
                {
                    return selection;
                }
            }
        }

        var prompt = new TextPrompt<string>($"{label}:");
        if (!required)
        {
            prompt.AllowEmpty();
        }

        var value = console.Prompt(prompt);
        return string.IsNullOrEmpty(value) && !required ? null : value;
    }

    // ---------------- Pure helpers (unit-tested) ----------------

    /// <summary>
    /// Flattens a JSON-Schema object into an ordered list of elicit-able properties. Returns an
    /// empty list when the schema is null, not an object, or declares no <c>properties</c>.
    /// </summary>
    public static IReadOnlyList<SchemaProperty> PlanProperties(JsonNode? inputSchema)
    {
        var list = new List<SchemaProperty>();
        if (inputSchema is not JsonObject root || root["properties"] is not JsonObject properties)
        {
            return list;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (root["required"] is JsonArray requiredArray)
        {
            foreach (var entry in requiredArray)
            {
                if (entry is JsonValue value && value.TryGetValue<string>(out var name))
                {
                    required.Add(name);
                }
            }
        }

        foreach (var (name, schema) in properties)
        {
            var schemaObject = schema as JsonObject;
            var enumValues = (schemaObject?["enum"] as JsonArray)?
                .Where(node => node is not null)
                .Select(node => node!.DeepClone())
                .ToList();

            list.Add(new SchemaProperty(
                Name: name,
                Type: ExtractType(schemaObject),
                Required: required.Contains(name),
                HasDefault: schemaObject?["default"] is not null,
                Default: schemaObject?["default"]?.DeepClone(),
                EnumValues: enumValues is { Count: > 0 } ? enumValues : null,
                Description: GetString(schemaObject, "description")));
        }

        return list;
    }

    /// <summary>
    /// Converts a raw string answer into the JSON node implied by the declared schema type.
    /// Throws (<see cref="FormatException"/> / <see cref="System.Text.Json.JsonException"/>)
    /// when the input can't be represented as the requested type - callers wrap this in prompt
    /// validation so the user simply re-enters.
    /// </summary>
    public static JsonNode? CoerceScalar(string raw, string? type)
    {
        switch (type)
        {
            case "boolean":
                return JsonValue.Create(ParseBool(raw));
            case "integer":
                return JsonValue.Create(long.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture));
            case "number":
                return JsonValue.Create(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
            case "array":
            case "object":
                return ParseJsonOfKind(raw, type);
            case "string":
                return JsonValue.Create(raw);
            default:
                // No declared type: accept any valid JSON literal, else treat as a plain string.
                try
                {
                    return JsonNode.Parse(raw);
                }
                catch
                {
                    return JsonValue.Create(raw);
                }
        }
    }

    /// <summary>
    /// Renders a declared default into the editable string a <see cref="TextPrompt{T}"/> shows.
    /// Strings come back bare (no surrounding quotes) so the user edits the value, not the JSON.
    /// </summary>
    public static string DefaultToEditString(JsonNode? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value.ToJsonString();
    }

    /// <summary>
    /// Extracts the distinct variable names from an RFC 6570-style URI template, in first-seen
    /// order. Handles operator prefixes (<c>+#./;?&amp;</c>), comma-lists, explode (<c>*</c>) and
    /// prefix (<c>:n</c>) modifiers. e.g. <c>db://{table}/{id}{?q,lang}</c> -&gt;
    /// <c>[table, id, q, lang]</c>.
    /// </summary>
    public static IReadOnlyList<string> ExtractTemplateVariables(string uriTemplate)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(uriTemplate))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(uriTemplate, "\\{([^}]+)\\}"))
        {
            var inner = match.Groups[1].Value;
            if (inner.Length > 0 && "+#./;?&".IndexOf(inner[0]) >= 0)
            {
                inner = inner[1..];
            }

            foreach (var part in inner.Split(','))
            {
                var name = part.Trim();
                if (name.EndsWith('*'))
                {
                    name = name[..^1];
                }

                var colon = name.IndexOf(':');
                if (colon >= 0)
                {
                    name = name[..colon];
                }

                name = name.Trim();
                if (name.Length > 0 && seen.Add(name))
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Best-effort reconstruction of the non-interactive CLI command equivalent to the
    /// invocation the user just assembled - handy to copy/paste or script later.
    /// </summary>
    public static string BuildEquivalentCommand(string verb, string subject, JsonObject? arguments, string transport, string target)
    {
        var builder = new StringBuilder("mcplense ");
        builder.Append(verb).Append(' ').Append(QuoteArg(subject));

        var isHttp = string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase);
        if (isHttp && !string.IsNullOrEmpty(target))
        {
            builder.Append(' ').Append(target);
        }

        if (arguments is { Count: > 0 })
        {
            builder.Append(" --args '").Append(arguments.ToJsonString()).Append('\'');
        }

        if (!isHttp && !string.IsNullOrEmpty(target))
        {
            builder.Append(" -- ").Append(target);
        }

        return builder.ToString();
    }

    // ---------------- Internals ----------------

    private static (bool Include, JsonNode? Value) PromptForProperty(IAnsiConsole console, SchemaProperty property)
    {
        if (!string.IsNullOrWhiteSpace(property.Description))
        {
            console.MarkupLine($"[grey]{Markup.Escape(property.Name)}: {Markup.Escape(property.Description!)}[/]");
        }

        var typeLabel = property.Type ?? (property.EnumValues is { Count: > 0 } ? "enum" : "any");
        var marker = property.Required ? " [red]*[/]" : string.Empty;
        var label = $"[green]{Markup.Escape(property.Name)}[/]{marker} [grey]({Markup.Escape(typeLabel)})[/]";

        if (property.EnumValues is { Count: > 0 })
        {
            return PromptEnum(console, property, label);
        }

        if (string.Equals(property.Type, "boolean", StringComparison.Ordinal))
        {
            return PromptBoolean(console, property, label);
        }

        return PromptText(console, property, label);
    }

    private static (bool Include, JsonNode? Value) PromptEnum(IAnsiConsole console, SchemaProperty property, string label)
    {
        // Map the display string back to the original enum node so non-string enums round-trip.
        var byDisplay = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var node in property.EnumValues!)
        {
            var display = node is JsonValue value && value.TryGetValue<string>(out var s) ? s : node.ToJsonString();
            if (byDisplay.TryAdd(display, node))
            {
                ordered.Add(display);
            }
        }

        // Put the declared default first so Enter accepts it.
        if (property.HasDefault)
        {
            var defaultDisplay = DefaultToEditString(property.Default);
            if (ordered.Remove(defaultDisplay))
            {
                ordered.Insert(0, defaultDisplay);
            }
        }

        var choices = new List<string>();
        if (!property.Required && !property.HasDefault)
        {
            choices.Add(SkipChoice);
        }

        choices.AddRange(ordered);

        var selection = console.Prompt(new SelectionPrompt<string>().UseConverter(Markup.Escape).Title(label).PageSize(12).AddChoices(choices));
        if (selection == SkipChoice)
        {
            return (false, null);
        }

        return (true, byDisplay.TryGetValue(selection, out var chosen) ? chosen?.DeepClone() : JsonValue.Create(selection));
    }

    private static (bool Include, JsonNode? Value) PromptBoolean(IAnsiConsole console, SchemaProperty property, string label)
    {
        if (property.Required || property.HasDefault)
        {
            var fallback = property.HasDefault && TryGetBool(property.Default, out var b) && b;
            var value = console.Confirm(label, fallback);
            return (true, JsonValue.Create(value));
        }

        var selection = console.Prompt(new SelectionPrompt<string>().Title(label).AddChoices(SkipChoice, "true", "false"));
        if (selection == SkipChoice)
        {
            return (false, null);
        }

        return (true, JsonValue.Create(selection == "true"));
    }

    private static (bool Include, JsonNode? Value) PromptText(IAnsiConsole console, SchemaProperty property, string label)
    {
        var prompt = new TextPrompt<string>($"{label}:");
        if (property.HasDefault)
        {
            prompt.DefaultValue(DefaultToEditString(property.Default));
            prompt.ShowDefaultValue();
        }
        else if (!property.Required)
        {
            prompt.AllowEmpty();
        }

        prompt.Validate(input => Validate(input, property));

        var raw = console.Prompt(prompt);
        if (string.IsNullOrEmpty(raw) && !property.Required && !property.HasDefault)
        {
            return (false, null);
        }

        return (true, CoerceScalar(raw, property.Type));
    }

    private static ValidationResult Validate(string input, SchemaProperty property)
    {
        if (string.IsNullOrEmpty(input))
        {
            return !property.Required && !property.HasDefault
                ? ValidationResult.Success()
                : ValidationResult.Error("[red]A value is required.[/]");
        }

        try
        {
            CoerceScalar(input, property.Type);
            return ValidationResult.Success();
        }
        catch (Exception ex)
        {
            return ValidationResult.Error($"[red]Invalid {Markup.Escape(property.Type ?? "value")}: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static JsonNode? ParseJsonOfKind(string raw, string type)
    {
        var node = JsonNode.Parse(raw);
        return type switch
        {
            "array" when node is not JsonArray => throw new FormatException("Expected a JSON array, e.g. [1, 2, 3]."),
            "object" when node is not JsonObject => throw new FormatException("Expected a JSON object, e.g. {\"key\": \"value\"}."),
            _ => node
        };
    }

    private static bool ParseBool(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "y" or "on" => true,
        "false" or "0" or "no" or "n" or "off" => false,
        _ => throw new FormatException("Expected true or false.")
    };

    private static bool TryGetBool(JsonNode? node, out bool value)
    {
        if (node is JsonValue scalar)
        {
            if (scalar.TryGetValue<bool>(out value))
            {
                return true;
            }

            if (scalar.TryGetValue<string>(out var text) && bool.TryParse(text, out value))
            {
                return true;
            }
        }

        value = false;
        return false;
    }

    private static string? ExtractType(JsonObject? schema)
    {
        var type = schema?["type"];
        if (type is JsonValue value && value.TryGetValue<string>(out var single))
        {
            return single;
        }

        if (type is JsonArray array)
        {
            foreach (var entry in array)
            {
                if (entry is JsonValue item && item.TryGetValue<string>(out var name) && name != "null")
                {
                    return name;
                }
            }
        }

        return null;
    }

    private static string? GetString(JsonObject? obj, string key)
        => obj?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string QuoteArg(string value)
        => value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
