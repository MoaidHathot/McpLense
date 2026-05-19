namespace McpLense.Scanning;

/// <summary>
/// Central registry of every built-in <see cref="IScanCheck"/> shipping with the library.
/// Both the fluent builder (<see cref="ScanPipelineBuilder.AddDefaultChecks"/>) and the
/// DI extension (<see cref="McpLenseServiceCollectionExtensions.AddMcpLense"/>) call this
/// to populate the default check set.
/// </summary>
/// <remarks>
/// The list order matches the on-disk source-file order under <c>Scanning/Checks/</c>; the
/// pipeline does its own topological sort so list order only affects the report's
/// dictionary insertion order (which itself shouldn't matter for consumers reading JSON).
/// </remarks>
internal static class BuiltInChecks
{
    public static IReadOnlyList<IScanCheck> Create() =>
    [
        // Auth + identity surface (run early; everything else may depend on them).
        new Checks.AuthCheck(),
        new Checks.TransportCheck(),
        new Checks.StdioCheck(),

        // MCP-session-driven (depend on auth: need to know whether to open anonymous or via a profile).
        new Checks.ServerInfoCheck(),
        new Checks.ProtocolCheck(),
        new Checks.ToolsCheck(),
        new Checks.PromptsCheck(),
        new Checks.ResourcesCheck(),

        // OAuth deepening.
        new Checks.AuthorizationServersCheck(),

        // Security posture from the transport probe / fresh GETs.
        new Checks.TlsChainCheck(),
        new Checks.AuthenticatedHeadersCheck(),
        new Checks.CorsPreflightCheck(),
        new Checks.DcrEndpointCheck(),

        // Behavioural probes.
        new Checks.Behavior.CallNonExistentToolCheck(),
        new Checks.Behavior.ServerInitiatedObservationCheck(),

        // Roll-up metrics + content hashing run last; they consume earlier checks' outputs.
        new Checks.MetricsCheck(),
        new Checks.HashingCheck(),
    ];
}
