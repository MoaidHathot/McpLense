using System.Text.Json.Nodes;
using McpLense.Scanning;

namespace McpLense.TestPlugin;

/// <summary>
/// Minimal IScanCheck used by the plugin-loader unit tests. Has the parameterless
/// constructor the loader requires, returns deterministic data, declares no dependencies.
/// </summary>
public sealed class HelloPluginCheck : IScanCheck
{
    public string Id => "plugin.hello";
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();
    public bool IsEnabledByDefault => false;

    public Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var data = new JsonObject { ["greeting"] = "hello from a plugin" };
        return Task.FromResult(new CheckOutcome(Ran: true, Data: data, Error: null));
    }
}

/// <summary>
/// A second type that LOOKS plugin-like but has no parameterless constructor.
/// The loader must skip it silently rather than failing the whole assembly.
/// </summary>
public sealed class NeedsArgsCheck : IScanCheck
{
    private readonly string _id;
    public NeedsArgsCheck(string id) { _id = id; }
    public string Id => _id;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();
    public bool IsEnabledByDefault => false;
    public Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
        => Task.FromResult(CheckOutcome.OkNoData);
}
