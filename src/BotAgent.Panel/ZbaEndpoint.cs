using System.Text.Json.Nodes;

namespace BotAgent.Panel;

/// <summary>
/// ZBA v1 HTTP 适配层。它只做四件事：路由、认证/授权、闭包 JSON、调用宿主实现的 <see cref="IZbaBackend" />。
/// 不读取数据库、不读时钟、不构造 HttpClient；宿主把这些责任留在边界外。
/// </summary>
public sealed partial class ZbaEndpoint
{
    private readonly IZbaBackend _backend;
    private readonly ZbaSecurity _security;
    private readonly ZbaOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _serverVersion;
    private readonly string _instanceId;
    private readonly Action<string>? _log;

    /// <summary>本进程实际提供的能力；客户端据此判断新入口是否可用，不靠版本号猜。</summary>
    private static readonly string[] Features =
    {
        "sessions.list",
        "sessions.ensure",
        "tasks.result",
        "tasks.find",
        "tasks.receipts",
        "plugins.catalog",
        "plugins.installed",
        "plugins.install",
        "plugins.update",
        "plugins.uninstall",
        "plugins.operations.get",
        "plugins.settings.read",
        "plugins.settings.save",
        "providers.models"
    };

    public ZbaEndpoint(
        IZbaBackend backend,
        ZbaOptions options,
        Func<DateTimeOffset> now,
        string serverVersion,
        string instanceId,
        Action<string>? log = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _serverVersion = serverVersion;
        _instanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
        _log = log;
        _security = new ZbaSecurity(options, now);
    }

    public IReadOnlyList<ZbaRouteInfo> Routes => ZbaRoutes.All;

    public int MaxBodyBytes => _options.MaxBodyBytes;

    public async ValueTask<ZbaResponse> ProcessAsync(ZbaRequest request, CancellationToken ct = default)
    {
        var requestId = "zr-" + Guid.NewGuid().ToString("N")[..16];
        ZbaRouteMatch? match = null;
        try
        {
            var path = NormalizePath(request.Path);
            var method = request.Method.ToUpperInvariant();
            match = ZbaRoutes.Match(method, path);
            if (match is null)
            {
                if (ZbaRoutes.HasPath(path)) return MethodNotAllowed(requestId, path);
                return Error(404, "not_found", "路径不存在。", requestId: requestId);
            }

            if (method == "GET" && request.Body.Length != 0)
                return Error(400, "body_not_allowed", "GET 请求不接受正文。", requestId: requestId);

            ZbaPrincipal? principal = null;
            if (!match.Route.Public)
            {
                var auth = _security.Authorize(request, match.Route.Scope);
                if (!auth.Ok)
                    return Error(auth.Status, auth.Code ?? "unauthorized", auth.Message ?? "未认证。",
                        requestId: requestId, authenticate: auth.Status == 401);
                principal = auth.Principal;
            }

            var query = ZbaQuery.Parse(request.QueryString);
            query.AllowOnly(ZbaRoutes.QueryNames(match.Route.OperationId));
            var response = match.Route.OperationId switch
            {
                "health" => await HandleHealthAsync(requestId, ct).ConfigureAwait(false),
                "meta" => MetaResponse(requestId),
                "openapi" => OpenApiResponse(requestId),
                "status" => await HandleStatusAsync(CreateCaller(principal!), requestId, ct).ConfigureAwait(false),
                "sessions.list" => await HandleSessionsListAsync(CreateCaller(principal!), query, requestId, ct).ConfigureAwait(false),
                "sessions.ensure" => await HandleSessionsEnsureAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "capabilities.list" => await HandleCapabilitiesListAsync(CreateCaller(principal!), query, requestId, ct).ConfigureAwait(false),
                "capabilities.inspect" => await HandleCapabilityInspectAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "capabilities.validate" => await HandleCapabilityValidateAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "capabilities.execute" => await HandleCapabilityExecuteAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "capabilities.query" => await HandleCapabilityQueryAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "capabilities.cancel" => await HandleCapabilityCancelAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "configuration.read" => await HandleConfigurationReadAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "configuration.save" => await HandleConfigurationSaveAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "configuration.find" => await HandleConfigurationFindAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "tasks.list" => await HandleTasksListAsync(CreateCaller(principal!), query, requestId, ct).ConfigureAwait(false),
                "tasks.submit" => await HandleTaskSubmitAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "tasks.find" => await HandleTaskFindAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "tasks.get" => await HandleTaskGetAsync(CreateCaller(principal!), match.Values["id"], requestId, ct).ConfigureAwait(false),
                "tasks.result" => await HandleTaskResultAsync(CreateCaller(principal!), match.Values["id"], requestId, ct).ConfigureAwait(false),
                "tasks.cancel" => await HandleTaskCancelAsync(CreateCaller(principal!), match.Values["id"], requestId, ct).ConfigureAwait(false),
                "approvals.list" => await HandleApprovalsListAsync(CreateCaller(principal!), requestId, ct).ConfigureAwait(false),
                "approvals.decide" => await HandleApprovalDecideAsync(CreateCaller(principal!), match.Values["id"], request, requestId, ct).ConfigureAwait(false),
                "events.read" => await HandleEventsAsync(CreateCaller(principal!), query, requestId, ct).ConfigureAwait(false),
                "plugins.catalog" => await HandlePluginCatalogAsync(CreateCaller(principal!), query, requestId, ct).ConfigureAwait(false),
                "plugins.installed" => await HandlePluginInstalledAsync(CreateCaller(principal!), requestId, ct).ConfigureAwait(false),
                "plugins.sources.resolve" => await HandlePluginSourceResolveAsync(CreateCaller(principal!), request, requestId, ct).ConfigureAwait(false),
                "plugins.install" => await HandlePluginMutationAsync(CreateCaller(principal!), request, requestId, ct, update: false).ConfigureAwait(false),
                "plugins.update" => await HandlePluginMutationAsync(CreateCaller(principal!), request, requestId, ct, update: true).ConfigureAwait(false),
                "plugins.uninstall" => await HandlePluginUninstallAsync(CreateCaller(principal!), match.Values["id"], request, requestId, ct).ConfigureAwait(false),
                "plugins.operations.get" => await HandlePluginOperationAsync(CreateCaller(principal!), match.Values["key"], requestId, ct).ConfigureAwait(false),
                "plugins.settings.read" => await HandlePluginSettingsReadAsync(CreateCaller(principal!), match.Values["id"], requestId, ct).ConfigureAwait(false),
                "plugins.settings.save" => await HandlePluginSettingsSaveAsync(CreateCaller(principal!), match.Values["id"], request, requestId, ct).ConfigureAwait(false),
                "providers.models" => await HandleProviderModelsAsync(CreateCaller(principal!), match.Values["key"], requestId, ct).ConfigureAwait(false),
                _ => Error(404, "not_found", "路径不存在。", requestId: requestId)
            };

            return Attach(response, requestId);
        }
        catch (ZbaBadRequestException error)
        {
            return Error(400, error.Code, "请求无效。", requestId: requestId);
        }
        catch (ZbaBodyTooLargeException)
        {
            return Error(413, "body_too_large", "请求正文超过上限。", requestId: requestId);
        }
        catch (ZbaBackendException error)
        {
            return Error(error.Status, error.Code, error.Message, error.Reason, requestId: requestId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Error(503, "cancelled", "请求已取消。", requestId: requestId);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"[Zba] route={match?.Route.OperationId ?? "unknown"} status=500 type={exception.GetType().Name} request={requestId}");
            return Error(500, "internal_error", "服务内部错误。", requestId: requestId);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static ConfigurationCaller CreateCaller(ZbaPrincipal principal)
        => principal.Scope == ZbaScope.Admin
            ? ZbaCallers.Admin(principal.Subject)
            : ZbaCallers.Read(principal.Subject);

    private ZbaResponse MethodNotAllowed(string requestId, string path)
    {
        var methods = ZbaRoutes.All
            .Where(route => route.Template == path || MatchesTemplate(route.Template, path))
            .Select(route => route.Method)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var response = Error(405, "method_not_allowed", "方法不允许。", requestId: requestId);
        if (methods.Length == 0) return response;
        var headers = new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["Allow"] = string.Join(", ", methods)
        };
        return response with { Headers = headers };
    }

    private static bool MatchesTemplate(string template, string path)
    {
        var templateParts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (templateParts.Length != pathParts.Length) return false;
        for (var index = 0; index < templateParts.Length; index++)
        {
            var expected = templateParts[index];
            if (!expected.StartsWith('{') && !expected.Equals(pathParts[index], StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private ZbaResponse MetaResponse(string requestId)
        => Json(new ZbaMeta(
            ZbaContract.Version,
            _serverVersion,
            "bearer",
            new[] { "read", "admin" },
            Sse: false,
            McpManagement: false,
            DockerManagement: false,
            _options.MaxBodyBytes,
            _instanceId,
            Features), requestId);

    private ZbaResponse OpenApiResponse(string requestId)
        => Json(ZbaOpenApi.Build(), requestId);

    private async ValueTask<ZbaResponse> HandleHealthAsync(string requestId, CancellationToken ct)
    {
        var health = await _backend.GetHealthAsync(ct).ConfigureAwait(false);
        health = health with { Configured = _security.Configured };
        return Json(health, requestId, health.Status == "unhealthy" ? 503 : 200);
    }

    private async ValueTask<ZbaResponse> HandleStatusAsync(
        ConfigurationCaller caller, string requestId, CancellationToken ct)
    {
        var status = await _backend.GetStatusAsync(caller, ct).ConfigureAwait(false);
        return Json(status, requestId);
    }

    private ZbaResponse Json<T>(T value, string requestId, int status = 200)
        => new(status, "application/json; charset=utf-8", ZbaJson.Serialize(value), StandardHeaders(requestId));

    private ZbaResponse Error(
        int status,
        string code,
        string message,
        string? reason = null,
        int? backendCode = null,
        string? requestId = null,
        bool authenticate = false)
    {
        var headers = StandardHeaders(requestId ?? string.Empty);
        if (authenticate) headers["WWW-Authenticate"] = "Bearer";
        var error = new ZbaErrorBody(new ZbaError(code, message, reason, backendCode));
        return new(status, "application/json; charset=utf-8", ZbaJson.Serialize(error), headers);
    }

    private static ZbaResponse Attach(ZbaResponse response, string requestId)
    {
        var headers = new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in StandardHeaders(requestId))
            headers.TryAdd(name, value);
        return response with { Headers = headers };
    }

    private static Dictionary<string, string> StandardHeaders(string requestId)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Zba-Contract"] = ZbaContract.Version,
            ["X-Request-Id"] = requestId,
            ["Cache-Control"] = "no-store",
            ["Vary"] = "Authorization"
        };

    private T Body<T>(ZbaRequest request) => ZbaJson.Parse<T>(request.Body, _options.MaxBodyBytes);

    private static int Status(ConfigurationResultCode code) => code switch
    {
        ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges => 200,
        ConfigurationResultCode.Forbidden => 403,
        ConfigurationResultCode.NotFound => 404,
        ConfigurationResultCode.ValidationFailed or ConfigurationResultCode.UnsupportedContractRevision => 400,
        ConfigurationResultCode.VersionConflict or ConfigurationResultCode.IdempotencyConflict
            or ConfigurationResultCode.KeyGenerationMismatch or ConfigurationResultCode.ClockRegression
            or ConfigurationResultCode.CancelledBeforeCommit or ConfigurationResultCode.LegacyNeedsReview => 409,
        ConfigurationResultCode.KeyExpired => 410,
        ConfigurationResultCode.CapacityExceeded => 429,
        _ => 503
    };

    private static int Status(ManagedResultCode code) => code switch
    {
        ManagedResultCode.Ok => 200,
        ManagedResultCode.Forbidden => 403,
        ManagedResultCode.NotFound => 404,
        ManagedResultCode.ValidationFailed or ManagedResultCode.InvalidCursor
            or ManagedResultCode.UnsupportedContractRevision => 400,
        ManagedResultCode.UnsupportedAction => 422,
        ManagedResultCode.AmbiguousLegacyIdentity or ManagedResultCode.VersionConflict
            or ManagedResultCode.StaleCursor or ManagedResultCode.IdempotencyConflict
            or ManagedResultCode.KeyGenerationMismatch or ManagedResultCode.ClockRegression => 409,
        ManagedResultCode.KeyExpired => 410,
        ManagedResultCode.CapacityExceeded => 429,
        ManagedResultCode.StorageUnavailable => 503,
        _ => 502
    };
}
