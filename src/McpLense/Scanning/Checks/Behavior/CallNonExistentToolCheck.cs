using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace McpLense.Scanning.Checks.Behavior;

/// <summary>
/// Calls a tool name the server (presumably) doesn't expose and records the response
/// verbatim. Three structurally-distinct outcomes: tool-result-returned (with isError),
/// jsonrpc-error (with code/message), transport-error (with framework exception).
/// </summary>
internal sealed class CallNonExistentToolCheck : IScanCheck
{
    public string Id => "behavior.callNonExistentTool";
    public IReadOnlyList<string> DependsOn => new[] { "auth" };
    public bool IsEnabledByDefault => true;

    private const string NonExistentToolName = "__mcplense_audit_probe_nonexistent_tool__";

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var client = await CheckSessionHelpers.TryGetSessionAsync(context, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new CheckOutcome(Ran: true, Data: null, Error: context.SessionError ?? "No MCP session available.");
        }

        var fetchedVia = context.ActiveFetchedVia;

        try
        {
            var response = await client.CallToolAsync(NonExistentToolName, options: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            var probe = new CallNonExistentToolProbe(
                Attempted: true,
                ToolNameUsed: NonExistentToolName,
                FetchedVia: fetchedVia,
                Outcome: CallNonExistentToolOutcomes.ToolResultReturned,
                ToolResultIsError: CheckSessionHelpers.GetBoolProp(response, "IsError"),
                ToolResultJson: SerializeResponse(response));

            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(probe), Error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var typeName = ex.GetType().Name;
            var isProtocolError = typeName.Contains("McpProtocol", StringComparison.Ordinal)
                                  || typeName.Equals("McpException", StringComparison.Ordinal);

            if (isProtocolError)
            {
                int? errorCode = null;
                var raw = CheckSessionHelpers.GetProp(ex, "ErrorCode");
                if (raw is int i)
                {
                    errorCode = i;
                }
                else if (raw is not null && raw.GetType().IsEnum)
                {
                    errorCode = (int)Convert.ChangeType(raw, typeof(int));
                }

                var probe = new CallNonExistentToolProbe(
                    Attempted: true,
                    ToolNameUsed: NonExistentToolName,
                    FetchedVia: fetchedVia,
                    Outcome: CallNonExistentToolOutcomes.JsonRpcError,
                    JsonRpcErrorCode: errorCode,
                    JsonRpcErrorMessage: ex.Message);

                return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(probe), Error: null);
            }

            var transport = new CallNonExistentToolProbe(
                Attempted: true,
                ToolNameUsed: NonExistentToolName,
                FetchedVia: fetchedVia,
                Outcome: CallNonExistentToolOutcomes.TransportError,
                TransportError: $"{ex.GetType().Name}: {ex.Message}");
            return new CheckOutcome(Ran: true, Data: CheckSessionHelpers.ToNode(transport), Error: null);
        }
    }

    private static string? SerializeResponse(object response)
    {
        try
        {
            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = false });
        }
        catch
        {
            return response?.ToString();
        }
    }
}
