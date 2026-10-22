using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BotAgent.Mcp;

/// <summary>
/// Streamable HTTP MCP 客户端（协议冻结在 2025-11-25）。
///
/// 这里只做三件事：连接/发现、按显式授权清单调用、把失败映射成短原因码。
/// 授权、Schema 校验、预算与审计在 Engine 的 <c>McpToolUseCase</c> 里；本层再核对一次授权清单，
/// 防止调用方绕过（纵深防御）。不创建 socket：<see cref="HttpClient" /> 由宿主从唯一出网点注入，
/// 因而 SSRF 连接回调、DNS 固定与认证边界都沿用宿主那一套。
/// </summary>
public sealed class McpToolSource : IMcpToolSource
{
    private const string ProtocolVersion = "2025-11-25";
    private const int MaxConcurrentCalls = 8;
    private const int MaxConcurrentPerServer = 2;
    private const int MaxToolContentBytes = 2 * 1024;

    private readonly IReadOnlyList<McpServerOptions> _servers;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _calls = new(MaxConcurrentCalls, MaxConcurrentCalls);
    private readonly Dictionary<string, ServerState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private readonly object _routeGate = new();
    private int _disposed;

    public McpToolSource(IReadOnlyList<McpServerOptions> servers, HttpClient http, Action<string>? log = null)
    {
        _servers = servers ?? Array.Empty<McpServerOptions>();
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log;
        foreach (var server in _servers)
        {
            _states[server.Id] = new ServerState(server);
        }
    }

    public bool Enabled => Volatile.Read(ref _disposed) == 0 && _servers.Count > 0;

    public McpConnectionState State
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return McpConnectionState.Disposed;
            }

            if (_servers.Count == 0)
            {
                return McpConnectionState.Disabled;
            }

            var states = _states.Values.Select(state => state.Current).ToArray();
            if (states.Any(state => state == McpConnectionState.Ready))
            {
                return McpConnectionState.Ready;
            }

            if (states.Any(state => state == McpConnectionState.Connecting))
            {
                return McpConnectionState.Connecting;
            }

            return states.Any(state => state == McpConnectionState.Faulted)
                ? McpConnectionState.Faulted
                : McpConnectionState.Disconnected;
        }
    }

    public async Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return Array.Empty<McpToolInfo>();
        }

        var tools = new List<McpToolInfo>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in _servers)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0)
            {
                break;
            }

            try
            {
                var client = await EnsureClientAsync(server, ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                timeout.CancelAfter(server.CallTimeout);
                var listed = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
                foreach (var tool in listed)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        break;
                    }

                    if (!server.AllowedTools.Contains(tool.Name, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    var info = Project(server, tool, claimed);
                    if (info is not null)
                    {
                        tools.Add(info);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
            {
                break;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
            {
                break;
            }
            catch (Exception ex)
            {
                MarkFaulted(server, ex);
            }
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            _routes.Clear();
            return Array.Empty<McpToolInfo>();
        }

        ReplaceRoutes(tools);
        _log?.Invoke($"[Mcp] 发现完成：servers={_servers.Count} tools={tools.Count}");
        return tools;
    }

    public async Task<McpCallResult> CallAsync(string qualifiedName, JsonObject arguments, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Fail("mcp_disposed", unknown: false);
        }

        if (string.IsNullOrWhiteSpace(qualifiedName) || !_routes.TryGetValue(qualifiedName, out var route))
        {
            return Fail("mcp_unknown_tool", unknown: false);
        }

        if (!_states.TryGetValue(route.ServerId, out var state) ||
            !state.Server.AllowedTools.Contains(route.ToolName, StringComparer.Ordinal))
        {
            return Fail("mcp_not_authorized", unknown: false);
        }

        if (!await _calls.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return Fail("mcp_busy", unknown: false);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            if (!await state.Gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return Fail("mcp_busy", unknown: false);
            }

            try
            {
                var client = await EnsureClientAsync(state.Server, ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                timeout.CancelAfter(state.Server.CallTimeout);
                var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    arguments?.ToJsonString() ?? "{}") ?? new Dictionary<string, object?>();
                var result = await client.CallToolAsync(route.ToolName, payload, cancellationToken: timeout.Token)
                    .ConfigureAwait(false);
                return MapResult(state.Server, route, result, started);
            }
            finally
            {
                state.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
        {
            return Fail("mcp_disposed", unknown: true, started);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            return Fail("mcp_disposed", unknown: true, started);
        }
        catch (Exception) when (Volatile.Read(ref _disposed) != 0)
        {
            // 释放与在途调用并发时，传输层可能先以断连收口；对外统一为已释放、结果未知。
            return Fail("mcp_disposed", unknown: true, started);
        }
        catch (OperationCanceledException)
        {
            // 本地超时与调用者取消必须分开：远端可能已经执行，调用方不得自动重放。
            MarkFaulted(state.Server, null);
            return Fail("mcp_timeout", unknown: true, started);
        }
        catch (Exception ex)
        {
            MarkFaulted(state.Server, ex);
            return Fail(Classify(ex), unknown: true, started);
        }
        finally
        {
            _calls.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _lifetime.Cancel(); } catch { }
        foreach (var state in _states.Values)
        {
            var ownsGate = false;
            try
            {
                // 与连接创建共用同一把门：等已开始的初始化被 lifetime 取消并退出。
                // 即使等待超时，后面的状态锁仍与发布互斥，迟到客户端不会被发布。
                ownsGate = await state.ConnectGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                McpClient? client;
                lock (state.Sync)
                {
                    client = state.Client;
                    state.Client = null;
                }

                if (client is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await client.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // 停机收口不能被远端拖住；连接由 McpClient/HttpClient 的释放兜底。
            }
            finally
            {
                lock (state.Sync)
                {
                    state.Client = null;
                    state.Set(McpConnectionState.Disposed);
                }

                if (ownsGate)
                {
                    state.ConnectGate.Release();
                }
            }
        }

        lock (_routeGate)
        {
            _routes.Clear();
        }
        // 不在停机时 Dispose lifetime：晚到的连接/发现仍可能读取它的 Token；
        // 该 CTS 不带定时器，交给 GC 收口即可，避免 ObjectDisposedException 掩盖真实状态。
    }

    private async Task<McpClient> EnsureClientAsync(McpServerOptions server, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(McpToolSource));
        }

        var state = _states[server.Id];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await state.ConnectGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (state.Sync)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(McpToolSource));
                }

                if (state.Client is { } existing)
                {
                    // 缓存命中不是新连接：恢复 Ready，不能把成功调用后的状态留在 Connecting。
                    state.Set(McpConnectionState.Ready);
                    return existing;
                }
            }

            var previous = state.Current;
            state.Set(McpConnectionState.Connecting);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(server.Token))
            {
                headers["Authorization"] = "Bearer " + server.Token;
            }

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Endpoint, UriKind.Absolute),
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = "mcp:" + server.Id,
                AdditionalHeaders = headers,
                ConnectionTimeout = server.CallTimeout,
                EnableStandaloneGetStream = false,
                MaxReconnectionAttempts = 1,
                DefaultReconnectionInterval = TimeSpan.FromSeconds(1),
            }, _http, NullLoggerFactory.Instance, ownsHttpClient: false);

            McpClient client;
            try
            {
                client = await McpClient.CreateAsync(transport, new McpClientOptions
                {
                    ProtocolVersion = ProtocolVersion,
                    ClientInfo = new Implementation { Name = "bot-agent", Version = "1.1.0" },
                    InitializationTimeout = server.CallTimeout,
                }, NullLoggerFactory.Instance, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用者取消不是故障：没有建立连接时恢复到尝试前的状态，不能永久停在 Connecting。
                if (state.Client is null && Volatile.Read(ref _disposed) == 0)
                {
                    state.Set(previous == McpConnectionState.Ready
                        ? McpConnectionState.Disconnected
                        : previous);
                }

                try { await transport.DisposeAsync().ConfigureAwait(false); } catch { }
                throw;
            }
            catch
            {
                try { await transport.DisposeAsync().ConfigureAwait(false); } catch { }
                throw;
            }

            var published = false;
            lock (state.Sync)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    state.Client = client;
                    state.Set(McpConnectionState.Ready);
                    published = true;
                }
            }

            if (!published)
            {
                // 释放与初始化赛跑：即使 SDK 忽略了取消，也拒绝发布迟到客户端。
                await DisposeQuietlyAsync(client, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(McpToolSource));
            }

            _log?.Invoke($"[Mcp] 已连接 server={server.Id}");
            return client;
        }
        finally
        {
            state.ConnectGate.Release();
        }
    }

    private McpToolInfo? Project(McpServerOptions server, McpClientTool tool, HashSet<string> claimed)
    {
        var description = Clean(tool.Description, McpLimits.MaxDescriptionLength);
        string schema;
        try
        {
            schema = tool.JsonSchema.GetRawText();
        }
        catch
        {
            return null;
        }

        if (Encoding.UTF8.GetByteCount(schema) > McpLimits.MaxSchemaBytes)
        {
            _log?.Invoke($"[Mcp] 丢弃 schema 过大的工具 server={server.Id} tool={Clean(tool.Name, 64)}");
            return null;
        }

        var qualified = Qualify(server.Id, tool.Name, claimed);
        if (qualified.Length > McpLimits.MaxToolNameLength)
        {
            return null;
        }

        return new McpToolInfo(
            tool.Name,
            qualified,
            description,
            schema,
            server.Id,
            tool.ProtocolTool.Annotations?.ReadOnlyHint == true);
    }

    private static string Qualify(string serverId, string toolName, HashSet<string> claimed)
    {
        // 模型侧工具名统一小写：ServerAgentRunner.ParseStep 会 ToLowerInvariant，
        // 保留大小写会让大写工具“看得见、调不到”。远端原名仍保存在 Route 里。
        var baseName = "mcp." + serverId.ToLowerInvariant() + "." + Sanitize(toolName).ToLowerInvariant();
        var candidate = baseName;
        if (claimed.Add(candidate))
        {
            return candidate;
        }

        // 清洗后重名：加一个稳定的短哈希，不改变远端原名。
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(toolName)))[..6].ToLowerInvariant();
        candidate = baseName + "." + hash;
        claimed.Add(candidate);
        return candidate;
    }

    private static string Sanitize(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
        }

        var name = builder.Length == 0 ? "tool" : builder.ToString();
        return name.Length > 64 ? name[..64] : name;
    }

    private McpCallResult MapResult(McpServerOptions server, Route route, CallToolResult result, long started)
    {
        var limit = Math.Clamp(server.MaxResultBytes, 1024, McpLimits.MaxResultBytes);
        var text = TextContent(result.Content, limit, out var oversized);
        if (oversized)
        {
            return Fail("mcp_result_too_large", unknown: false, started,
                $"server={server.Id};tool={route.ToolName};bytes>limit;limit={limit}");
        }

        var structured = Structured(result);
        if (text.Length == 0 && structured.Length > 0)
        {
            if (Encoding.UTF8.GetByteCount(structured) + 8 > limit)
            {
                return Fail("mcp_result_too_large", unknown: false, started,
                    $"server={server.Id};tool={route.ToolName};bytes>limit;limit={limit}");
            }

            text = "结构化结果：" + structured;
        }

        var omitted = result.Content?.Count(block => block is not TextContentBlock) ?? 0;
        if (omitted > 0)
        {
            text += (text.Length > 0 ? "\n" : string.Empty) + $"（已省略 {omitted} 个非文本内容块）";
        }

        if (text.Length == 0)
        {
            text = "（工具没有返回文本内容）";
        }

        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > limit)
        {
            return Fail("mcp_result_too_large", unknown: false, started,
                $"server={server.Id};tool={route.ToolName};bytes={bytes};limit={limit}");
        }

        var failed = result.IsError == true;
        var detail = $"server={server.Id};tool={route.ToolName};status={(failed ? "tool_error" : "ok")}"
                     + $";bytes={bytes};ms={Elapsed(started)};unknown=0";
        if (failed)
        {
            return new McpCallResult(false, "mcp_tool_error", Clip(text, MaxToolContentBytes), detail,
                false, bytes, Elapsed(started));
        }

        return new McpCallResult(true, "ok", text, detail, false, bytes, Elapsed(started));
    }

    /// <summary>逐块拼接文本并在达到上限时立即放弃，避免把超大结果再拼成一个更大的字符串。</summary>
    private static string TextContent(IList<ContentBlock>? blocks, int limit, out bool oversized)
    {
        oversized = false;
        if (blocks is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var block in blocks.OfType<TextContentBlock>())
        {
            var piece = block.Text ?? string.Empty;
            var add = Encoding.UTF8.GetByteCount(piece) + (builder.Length > 0 ? 1 : 0);
            if (bytes + add > limit)
            {
                oversized = true;
                return string.Empty;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(piece);
            bytes += add;
        }

        return builder.ToString().Trim();
    }

    private static string Structured(CallToolResult result)
    {
        try
        {
            return result.StructuredContent is { } structured ? structured.GetRawText() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ReplaceRoutes(IReadOnlyList<McpToolInfo> tools)
    {
        lock (_routeGate)
        {
            _routes.Clear();
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            foreach (var tool in tools)
            {
                _routes[tool.QualifiedName] = new Route(tool.ServerId, tool.Name);
            }
        }
    }

    private void MarkFaulted(McpServerOptions server, Exception? exception)
    {
        if (!_states.TryGetValue(server.Id, out var state))
        {
            return;
        }

        McpClient? failed;
        lock (state.Sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            failed = state.Client;
            state.Client = null;
            state.Set(McpConnectionState.Faulted);
        }

        _ = DisposeQuietlyAsync(failed);
        _log?.Invoke($"[Mcp] server={server.Id} faulted={Classify(exception)}");
    }

    private static async Task DisposeQuietlyAsync(McpClient? client, TimeSpan? timeout = null)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            if (timeout is { } limit)
            {
                await client.DisposeAsync().AsTask().WaitAsync(limit).ConfigureAwait(false);
            }
            else
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // 释放失败不改变已经判定的事实。
        }
    }

    private static string Classify(Exception? exception) => exception switch
    {
        null => "transport",
        HttpRequestException => "mcp_transport",
        ClientTransportClosedException => "mcp_transport",
        McpException => "mcp_protocol",
        JsonException => "mcp_protocol",
        _ => "mcp_internal",
    };

    private static McpCallResult Fail(string reason, bool unknown, long started = 0, string? detail = null)
        => new(false, reason, null, detail ?? $"status={reason};unknown={(unknown ? 1 : 0)}",
            unknown, 0, started == 0 ? 0 : Elapsed(started));

    private static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var clean = new string(text.Where(ch => !char.IsControl(ch) || ch is '\n' or '\t').ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..max];
    }

    private static string Clip(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private static long Elapsed(long started)
        => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private readonly record struct Route(string ServerId, string ToolName);

    private sealed class ServerState
    {
        public ServerState(McpServerOptions server)
        {
            Server = server;
            ConnectGate = new SemaphoreSlim(1, 1);
            Gate = new SemaphoreSlim(MaxConcurrentPerServer, MaxConcurrentPerServer);
        }

        public McpServerOptions Server { get; }
        public SemaphoreSlim ConnectGate { get; }
        public SemaphoreSlim Gate { get; }
        public object Sync { get; } = new();
        public McpClient? Client { get; set; }
        private volatile McpConnectionState _current = McpConnectionState.Disconnected;
        public McpConnectionState Current => _current;

        public void Set(McpConnectionState state) => _current = state;
    }
}
