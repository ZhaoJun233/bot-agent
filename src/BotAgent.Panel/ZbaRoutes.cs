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
        new("GET", "/zba/v1/sessions", ZbaScope.Read, false, "sessions.list", "List server agent sessions for a workspace source", null, "ZbaSessionPage"),
        new("POST", "/zba/v1/sessions/ensure", ZbaScope.Admin, false, "sessions.ensure", "Ensure the current server agent session", "ZbaSessionEnsureRequest", "ZbaSessionEnsureResult"),
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
        new("POST", "/zba/v1/tasks/find", ZbaScope.Admin, false, "tasks.find", "Find a submission receipt by idempotency key", "ZbaTaskFindRequest", "ZbaTaskFindResult"),
        new("GET", "/zba/v1/tasks/{id}", ZbaScope.Read, false, "tasks.get", "Read one task terminal or running state", null, "ZbaTaskSummary"),
        new("GET", "/zba/v1/tasks/{id}/result", ZbaScope.Admin, false, "tasks.result", "Read a bounded result summary for a panel task", null, "ZbaTaskResult"),
        new("POST", "/zba/v1/tasks/{id}/cancel", ZbaScope.Admin, false, "tasks.cancel", "Request cancellation without claiming remote success", null, "ZbaTaskCancelResult"),
        new("GET", "/zba/v1/approvals", ZbaScope.Read, false, "approvals.list", "List pending approvals with redacted summaries", null, "ZbaApprovalSummaryList"),
        new("POST", "/zba/v1/approvals/{id}/decide", ZbaScope.Admin, false, "approvals.decide", "Approve or reject one pending request", "ZbaApprovalDecision", "ZbaApprovalDecisionResult"),
        new("GET", "/zba/v1/events", ZbaScope.Read, false, "events.read", "Read ordered events after a sequence or require resync", null, "ZbaEventPage"),
        new("GET", "/zba/v1/plugins/catalog", ZbaScope.Read, false, "plugins.catalog", "Browse the configured plugin marketplace catalog", null, "ZbaPluginCatalog"),
        new("GET", "/zba/v1/plugins/installed", ZbaScope.Read, false, "plugins.installed", "List installed plugin packages with real state", null, "ZbaInstalledPlugins"),
        new("POST", "/zba/v1/plugins/sources/resolve", ZbaScope.Admin, false, "plugins.sources.resolve", "Resolve one pinned public GitHub release into a manifest", "ZbaPluginSourceResolveRequest", "ZbaPluginSourceResolution"),
        new("POST", "/zba/v1/plugins/install", ZbaScope.Admin, false, "plugins.install", "Stage a plugin install; takes effect after restart", "ZbaPluginMutationRequest", "ZbaPluginOperationResult"),
        new("POST", "/zba/v1/plugins/update", ZbaScope.Admin, false, "plugins.update", "Stage a plugin update; takes effect after restart", "ZbaPluginMutationRequest", "ZbaPluginOperationResult"),
        new("POST", "/zba/v1/plugins/{id}/uninstall", ZbaScope.Admin, false, "plugins.uninstall", "Stage a plugin uninstall; takes effect after restart", "ZbaPluginUninstallRequest", "ZbaPluginOperationResult"),
        new("GET", "/zba/v1/plugins/operations/{key}", ZbaScope.Read, false, "plugins.operations.get", "Read one staged plugin operation by idempotency key", null, "ZbaPluginOperationResult"),
        new("GET", "/zba/v1/plugins/{id}/settings", ZbaScope.Read, false, "plugins.settings.read", "Read a plugin's declared settings without secret values", null, "ZbaPluginSettings"),
        new("PUT", "/zba/v1/plugins/{id}/settings", ZbaScope.Admin, false, "plugins.settings.save", "Save plugin settings with expected revision", "ZbaPluginSettingsSaveRequest", "ZbaPluginSettings"),
        new("GET", "/zba/v1/providers/{key}/models", ZbaScope.Read, false, "providers.models", "Discover upstream models for a saved provider route", null, "ZbaProviderModels")
    };

    internal static IReadOnlyList<ZbaRouteInfo> All => RouteTable;

    internal static string[] QueryNames(string operationId) => operationId switch
    {
        "sessions.list" => new[] { "sourceKey", "limit", "cursor" },
        "capabilities.list" => new[] { "kind", "limit", "cursor" },
        "tasks.list" => new[] { "limit", "cursor" },
        "events.read" => new[] { "after", "limit" },
        "plugins.catalog" => new[] { "q", "category" },
        _ => Array.Empty<string>()
    };

    internal static ZbaRouteMatch? Match(string method, string path)
    {
        foreach (var route in RouteTable)
        {
            if (!route.Method.Equals(method, StringComparison.Ordinal)) continue;
            if (TryMatch(route, path, out var values)) return new ZbaRouteMatch(route, values);
        }

        return null;
    }

    internal static bool HasPath(string path)
        => RouteTable.Any(route => TryMatch(route, path, out _));

    /// <summary>
    /// 只有插件的幂等键需要更宽的字符集：前端生成的键形如
    /// <c>plugin:install:id@version:issued:nonce</c>，含冒号与 <c>@</c>。
    /// 其余 <c>{id}</c> 段继续走严格字符集，避免放宽所有路径段。
    /// </summary>
    private static bool AllowsExtendedSegment(ZbaRouteInfo route) => route.OperationId is "plugins.operations.get";

    private static bool TryMatch(ZbaRouteInfo route, string path, out IReadOnlyDictionary<string, string> values)
    {
        var template = route.Template;
        var allowExtended = AllowsExtendedSegment(route);
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
                if (!TryDecodeSegment(actual, allowExtended, out var decoded)) return false;
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
        => TryDecodeSegment(value, false, out decoded);

    private static bool TryDecodeSegment(string value, bool allowExtended, out string decoded)
    {
        decoded = string.Empty;
        if (value.Length is 0 or > 512) return false;
        try
        {
            var result = Uri.UnescapeDataString(value);
            var maxLength = allowExtended ? 256 : 128;
            if (result.Length is 0 || result.Length > maxLength
                || result.Contains('/') || result.Contains('\\')) return false;
            if (!result.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or '~'
                || (allowExtended && character is ':' or '@')))
            {
                return false;
            }
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
