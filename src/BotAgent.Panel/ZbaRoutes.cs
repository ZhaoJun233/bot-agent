namespace BotAgent.Panel;

internal sealed record ZbaRouteMatch(ZbaRouteInfo Route, IReadOnlyDictionary<string, string> Values);

internal static class ZbaRoutes
{
    private static readonly ZbaRouteInfo[] RouteTable =
    {
        new("GET", "/zba/v1/health", ZbaScope.Read, true, "health", "Liveness / configured state", null, "ZbaHealth"),
        new("GET", "/zba/v1/meta", ZbaScope.Read, false, "meta", "Contract version and supported features", null, "ZbaMeta"),
        new("GET", "/zba/v1/openapi.json", ZbaScope.Read, false, "openapi", "Actual OpenAPI document for this server", null, "OpenApiDocument"),
        new("GET", "/zba/v1/status", ZbaScope.Read, false, "status", "Safe overall and per-component status", null, "ZbaStatus"),
        new("GET", "/zba/v1/capabilities", ZbaScope.Read, false, "capabilities.list", "Registered drivers, plugins, providers with real state", null, "ManagedPage"),
        new("POST", "/zba/v1/capabilities/inspect", ZbaScope.Read, false, "capabilities.inspect", "Inspect one stable capability key", "ZbaCapabilityInspectRequest", "ManagedInspectResult"),
        new("POST", "/zba/v1/capabilities/validate", ZbaScope.Admin, false, "capabilities.validate", "Validate capability configuration without writing", "ZbaConfigurationValidationRequest", "ManagedValidationResult"),
        new("POST", "/zba/v1/capabilities/execute", ZbaScope.Admin, false, "capabilities.execute", "Execute a supported management action", "ManagedExecuteRequest", "ManagedOperationResult"),
        new("POST", "/zba/v1/capabilities/query", ZbaScope.Read, false, "capabilities.query", "Query a management operation by key", "ManagedOperationKey", "ManagedOperationResult"),
        new("POST", "/zba/v1/capabilities/cancel", ZbaScope.Admin, false, "capabilities.cancel", "Cancel a management operation", "ManagedOperationKey", "ManagedCancellationResult"),
        new("POST", "/zba/v1/configuration/read", ZbaScope.Read, false, "configuration.read", "Read a safe configuration snapshot", "ZbaConfigurationReadRequest", "ConfigurationReadResult"),
        new("POST", "/zba/v1/configuration/save", ZbaScope.Admin, false, "configuration.save", "Batch save with expected version and idempotency key", "ZbaConfigurationSaveRequest", "ConfigurationSaveResult"),
        new("POST", "/zba/v1/configuration/find", ZbaScope.Admin, false, "configuration.find", "Find the final state of a submitted save", "ZbaConfigurationFindRequest", "ConfigurationFindResult"),
        new("GET", "/zba/v1/tasks", ZbaScope.Read, false, "tasks.list", "List agent task summaries", null, "ZbaTaskPage"),
        new("POST", "/zba/v1/tasks", ZbaScope.Admin, false, "tasks.submit", "Submit one agent task with an idempotency key", "ZbaTaskSubmission", "ZbaTaskAccepted"),
        new("GET", "/zba/v1/tasks/{id}", ZbaScope.Read, false, "tasks.get", "Read one task terminal or running state", null, "ZbaTaskSummary"),
        new("POST", "/zba/v1/tasks/{id}/cancel", ZbaScope.Admin, false, "tasks.cancel", "Request cancellation without claiming remote success", null, "ZbaTaskCancelResult"),
        new("GET", "/zba/v1/approvals", ZbaScope.Read, false, "approvals.list", "List pending approvals with redacted summaries", null, "ZbaApprovalSummaryList"),
        new("POST", "/zba/v1/approvals/{id}/decide", ZbaScope.Admin, false, "approvals.decide", "Approve or reject one pending request", "ZbaApprovalDecision", "ZbaApprovalDecisionResult"),
        new("GET", "/zba/v1/events", ZbaScope.Read, false, "events.read", "Read ordered events after a sequence or require resync", null, "ZbaEventPage")
    };

    internal static IReadOnlyList<ZbaRouteInfo> All => RouteTable;

    internal static string[] QueryNames(string operationId) => operationId switch
    {
        "capabilities.list" => new[] { "kind", "limit", "cursor" },
        "tasks.list" => new[] { "limit", "cursor" },
        "events.read" => new[] { "after", "limit" },
        _ => Array.Empty<string>()
    };

    internal static ZbaRouteMatch? Match(string method, string path)
    {
        foreach (var route in RouteTable)
        {
            if (!route.Method.Equals(method, StringComparison.Ordinal)) continue;
            if (TryMatch(route.Template, path, out var values)) return new ZbaRouteMatch(route, values);
        }

        return null;
    }

    internal static bool HasPath(string path)
        => RouteTable.Any(route => TryMatch(route.Template, path, out _));

    private static bool TryMatch(string template, string path, out IReadOnlyDictionary<string, string> values)
    {
        values = Empty;
        var templateParts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (templateParts.Length != pathParts.Length) return false;

        var captured = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < templateParts.Length; index++)
        {
            var expected = templateParts[index];
            var actual = pathParts[index];
            if (expected.StartsWith('{') && expected.EndsWith('}'))
            {
                var name = expected[1..^1];
                if (!TryDecodeSegment(actual, out var decoded)) return false;
                captured[name] = decoded;
            }
            else if (!expected.Equals(actual, StringComparison.Ordinal))
            {
                return false;
            }
        }

        values = captured;
        return true;
    }

    internal static bool TryDecodeSegment(string value, out string decoded)
    {
        decoded = string.Empty;
        if (value.Length is 0 or > 512) return false;
        try
        {
            var result = Uri.UnescapeDataString(value);
            if (result.Length is 0 or > 128 || result.Contains('/') || result.Contains('\\')) return false;
            if (!result.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~'))
                return false;
            decoded = result;
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
