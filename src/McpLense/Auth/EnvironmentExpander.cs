using System.Text;

namespace McpLense;

/// <summary>
/// Expands environment-variable references inside string config and CLI values.
///
/// Supported syntax:
/// <list type="bullet">
///   <item><c>env:VAR</c> &mdash; whole-string form. The value MUST start with <c>env:</c>;
///   the rest of the string is treated as a single variable name.</item>
///   <item><c>${VAR}</c> &mdash; substring form. May appear anywhere in the string.</item>
///   <item><c>${VAR:-default}</c> &mdash; substring form with a default. <c>default</c> is used when
///   <c>VAR</c> is unset or empty (bash <c>:-</c> semantics). May contain anything except <c>}</c>.</item>
///   <item><c>$$</c> &mdash; literal <c>$</c>. Any other bare <c>$</c> is preserved as-is.</item>
/// </list>
///
/// Errors include the supplied JSON path / CLI flag name to make config files easy to debug.
/// </summary>
public sealed class EnvironmentExpander
{
    private readonly Func<string, string?> _lookup;

    public EnvironmentExpander()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>
    /// Inject a custom variable lookup. Useful for tests and for embedding hosts that want to
    /// expand values against a non-process environment (e.g. a sealed secrets map).
    /// </summary>
    public EnvironmentExpander(Func<string, string?> lookup)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
    }

    /// <summary>
    /// Expands <paramref name="input"/> and returns the result.
    /// </summary>
    /// <param name="input">The raw value (may be null).</param>
    /// <param name="contextPath">A JSON path or CLI flag name used in error messages.</param>
    /// <exception cref="UserInputException">
    /// Thrown when an undefaulted variable is unset, when a syntax form is malformed,
    /// or when an <c>env:</c> prefix is missing the variable name.
    /// </exception>
    public string Expand(string? input, string contextPath)
    {
        if (input is null)
        {
            return string.Empty;
        }

        if (input.StartsWith("env:", StringComparison.Ordinal))
        {
            var name = input[4..];
            if (string.IsNullOrEmpty(name))
            {
                throw new UserInputException($"{contextPath}: 'env:' prefix requires a variable name.");
            }

            var value = _lookup(name);
            if (value is null)
            {
                throw new UserInputException(
                    $"{contextPath}: environment variable '{name}' (referenced by 'env:{name}') is not set.");
            }

            return value;
        }

        if (input.IndexOf('$') < 0)
        {
            return input;
        }

        var builder = new StringBuilder(input.Length);
        var index = 0;
        while (index < input.Length)
        {
            var ch = input[index];

            if (ch != '$')
            {
                builder.Append(ch);
                index++;
                continue;
            }

            // ch == '$'
            if (index + 1 < input.Length && input[index + 1] == '$')
            {
                builder.Append('$');
                index += 2;
                continue;
            }

            if (index + 1 >= input.Length || input[index + 1] != '{')
            {
                // Bare '$' - keep literal
                builder.Append('$');
                index++;
                continue;
            }

            // Found "${" - find the closing '}'
            var endBrace = input.IndexOf('}', index + 2);
            if (endBrace < 0)
            {
                throw new UserInputException(
                    $"{contextPath}: unterminated '${{' in value '{input}'.");
            }

            var inside = input.Substring(index + 2, endBrace - (index + 2));
            string name;
            string? defaultValue = null;

            var defaultSep = inside.IndexOf(":-", StringComparison.Ordinal);
            if (defaultSep >= 0)
            {
                name = inside[..defaultSep];
                defaultValue = inside[(defaultSep + 2)..];
            }
            else
            {
                name = inside;
            }

            if (string.IsNullOrEmpty(name))
            {
                throw new UserInputException(
                    $"{contextPath}: '${{}}' requires a variable name in '{input}'.");
            }

            var lookup = _lookup(name);
            string resolved;
            if (defaultValue is null)
            {
                if (lookup is null)
                {
                    throw new UserInputException(
                        $"{contextPath}: environment variable '{name}' (referenced by '${{{name}}}') is not set.");
                }

                resolved = lookup;
            }
            else
            {
                resolved = string.IsNullOrEmpty(lookup) ? defaultValue : lookup;
            }

            builder.Append(resolved);
            index = endBrace + 1;
        }

        return builder.ToString();
    }
}
