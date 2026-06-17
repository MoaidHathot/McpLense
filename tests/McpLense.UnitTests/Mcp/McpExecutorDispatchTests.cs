using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Mcp;

/// <summary>
/// Characterization tests for the <see cref="McpExecutor.ExecuteAsync"/> dispatch branches that
/// short-circuit BEFORE target resolution (diff), added before wiring the dictionary-driven
/// command-handler dispatch. They prove the diff branch is reached (no target-resolution error)
/// and its argument guard fires - exactly the wiring the handler split must preserve.
/// </summary>
public class McpExecutorDispatchTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static TargetOptions EmptyTarget()
        => new(
            ConfigPaths: [],
            ServerNames: [],
            ProfilePaths: [],
            DisplayName: null,
            Url: null,
            Transport: TransportPreference.Auto,
            Headers: new Dictionary<string, string>(),
            Command: null,
            CommandArguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            AuthOverrides: AuthOverrides.Empty);

    private static ParsedCommand DiffCommand(string? before, string? after)
        => new(
            Command: AppCommand.Diff,
            Subject: before,
            Arguments: null,
            Format: OutputFormat.Json,
            Timeout: TimeSpan.FromSeconds(5),
            Target: EmptyTarget(),
            ProgressEnabled: false,
            DiffBaselinePath: after);

    [Fact]
    public async Task Diff_MissingPaths_ThrowsArgumentGuard()
    {
        var ex = await Should.ThrowAsync<UserInputException>(
            () => McpExecutor.ExecuteAsync(DiffCommand(null, null), JsonOptions, CancellationToken.None));

        ex.Message.ShouldContain("diff");
    }

    [Fact]
    public async Task Diff_MissingBaselineFiles_ReachesDiffHandler()
    {
        // Both paths set but the files don't exist: the diff branch runs and BaselineWriter
        // surfaces a "not found" UserInputException. Proves we reached the diff handler rather
        // than failing earlier with a "Specify a target" resolution error.
        var ex = await Should.ThrowAsync<UserInputException>(
            () => McpExecutor.ExecuteAsync(
                DiffCommand("does-not-exist-before.json", "does-not-exist-after.json"),
                JsonOptions,
                CancellationToken.None));

        ex.Message.ShouldContain("not found");
    }
}
