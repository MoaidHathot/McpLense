using System;
using Xunit;

namespace McpLense.E2ETests;

/// <summary>
/// Skips the test unless the named environment variable equals "1" (case-insensitive "true" also accepted).
/// Used to gate live network tests against public MCP servers behind an opt-in flag.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkipUnlessEnvAttribute : FactAttribute
{
    public SkipUnlessEnvAttribute(string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName))
        {
            throw new ArgumentException("Environment variable name is required.", nameof(variableName));
        }

        var value = Environment.GetEnvironmentVariable(variableName);
        if (!IsTruthy(value))
        {
            Skip = $"Skipped: set environment variable '{variableName}=1' to enable this test.";
        }
    }

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.Ordinal) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
