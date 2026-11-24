using System.Text;
using BotAgent.Domain.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BotAgent.Mcp;

public sealed record McpProbeToolCandidate(
    string Name,
    string Description,
    bool ReadOnlyHint);

public sealed record McpProbeOutcome(
    bool Ok,
    string ReasonCode,
    IReadOnlyList<McpProbeToolCandidate> Tools);

/// <summary>
/// 支持运行时原子热切换的 MCP 工具来源代理（P4.3）：
/// 对上游 <see cref="McpToolUseCase"/> 透明实现 <see cref="IMcpToolSource"/>；
/// 热替换时先发布新实例，再安全释放旧实例（旧在途调用由 <see cref="McpToolSource.DisposeAsync"/> 有界收尾）。
/// </summary>
public sealed class ReloadableMcpToolSource : IMcpToolSource, IAsyncDisposable
{
    private const string ProtocolVersion = "2025-11-25";
    private readonly SemaphoreSlim _swapGate = new(1, 1);
    private IMcpToolSource _current;
    private int _disposed;

    public ReloadableMcpToolSource(IMcpToolSource initial)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    public IMcpToolSource Current => Volatile.Read(ref _current);

    public bool Enabled => Volatile.Read(ref _disposed) == 0 && Current.Enabled;

    public McpConnectionState State => Volatile.Read(ref _disposed) != 0 ? McpConnectionState.Disposed : Current.State;

    public Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(CancellationToken ct)
        => Current.DiscoverAsync(ct);

    public Task<McpCallResult> CallAsync(string qualifiedName, System.Text.Json.Nodes.JsonObject arguments, CancellationToken ct)
        => Current.CallAsync(qualifiedName, arguments, ct);

    public bool IsToolCurrent(McpToolInfo tool) => Volatile.Read(ref _disposed) == 0 && Current.IsToolCurrent(tool);

    public Task<McpCallResult> CallWithLeaseAsync(McpToolInfo tool, System.Text.Json.Nodes.JsonObject arguments, CancellationToken ct)
        => Current.CallWithLeaseAsync(tool, arguments, ct);

    public async Task SwapAsync(IMcpToolSource next, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (Volatile.Read(ref _disposed) != 0)
        {
            await DisposeQuietlyAsync(next).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(ReloadableMcpToolSource));
        }

        IMcpToolSource previous;
        await _swapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                await DisposeQuietlyAsync(next).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(ReloadableMcpToolSource));
            }
            previous = Interlocked.Exchange(ref _current, next);
        }
        finally
        {
            _swapGate.Release();
        }

        if (!ReferenceEquals(previous, next) && !ReferenceEquals(previous, NullMcpToolSource.Instance))
            await DisposeQuietlyAsync(previous).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IMcpToolSource current;
        await _swapGate.WaitAsync().ConfigureAwait(false);
        try
        {
            current = Interlocked.Exchange(ref _current, NullMcpToolSource.Instance);
        }
        finally
        {
            _swapGate.Release();
        }
        await DisposeQuietlyAsync(current).ConfigureAwait(false);
    }

    private static async ValueTask DisposeQuietlyAsync(IMcpToolSource source)
    {
        if (ReferenceEquals(source, NullMcpToolSource.Instance)) return;
        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>独立探测一个远端 Streamable HTTP MCP 服务端并列出其声明的工具清单（不污染运行态快照）。</summary>
    public static async Task<McpProbeOutcome> ProbeServerAsync(
        McpServerOptions server,
        HttpClient http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(http);
        if (!Uri.TryCreate(server.Endpoint, UriKind.Absolute, out var uri))
            return new McpProbeOutcome(false, "invalid_endpoint", Array.Empty<McpProbeToolCandidate>());

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(server.Token))
            headers["Authorization"] = "Bearer " + server.Token;

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = uri,
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "mcp-probe:" + server.Id,
            AdditionalHeaders = headers,
            ConnectionTimeout = server.CallTimeout,
            EnableStandaloneGetStream = false,
            MaxReconnectionAttempts = 0,
        }, http, NullLoggerFactory.Instance, ownsHttpClient: false);

        McpClient? client = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(server.CallTimeout);
            client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ProtocolVersion = ProtocolVersion,
                ClientInfo = new Implementation { Name = "bot-agent-probe", Version = "1.1.0" },
                InitializationTimeout = server.CallTimeout,
            }, NullLoggerFactory.Instance, timeout.Token).ConfigureAwait(false);

            var listed = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            var tools = new List<McpProbeToolCandidate>();
            foreach (var item in listed)
            {
                var name = (item.Name ?? string.Empty).Trim();
                if (name.Length == 0 || name.Length > 64) continue;
                var desc = CleanText(item.Description, McpLimits.MaxDescriptionLength);
                var ro = item.ProtocolTool.Annotations?.ReadOnlyHint == true;
                tools.Add(new McpProbeToolCandidate(name, desc, ro));
                if (tools.Count >= McpLimits.MaxTools) break;
            }
            return new McpProbeOutcome(true, "ok", tools);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new McpProbeOutcome(false, "probe_canceled", Array.Empty<McpProbeToolCandidate>());
        }
        catch (OperationCanceledException)
        {
            return new McpProbeOutcome(false, "mcp_timeout", Array.Empty<McpProbeToolCandidate>());
        }
        catch (HttpRequestException)
        {
            return new McpProbeOutcome(false, "mcp_unreachable", Array.Empty<McpProbeToolCandidate>());
        }
        catch
        {
            return new McpProbeOutcome(false, "mcp_probe_failed", Array.Empty<McpProbeToolCandidate>());
        }
        finally
        {
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            try { await transport.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private static string CleanText(string? raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var builder = new StringBuilder(Math.Min(raw.Length, max));
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) && ch != ' ') continue;
            builder.Append(ch);
            if (builder.Length >= max) break;
        }
        return builder.ToString().Trim();
    }

    private sealed class NullMcpToolSource : IMcpToolSource
    {
        public static readonly NullMcpToolSource Instance = new();
        public bool Enabled => false;
        public McpConnectionState State => McpConnectionState.Disposed;
        public Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>(Array.Empty<McpToolInfo>());
        public Task<McpCallResult> CallAsync(string qualifiedName, System.Text.Json.Nodes.JsonObject arguments, CancellationToken ct)
            => Task.FromResult(new McpCallResult(false, "mcp_disposed", string.Empty, "source_disposed", false, 0, 0));
        public bool IsToolCurrent(McpToolInfo tool) => false;
        public Task<McpCallResult> CallWithLeaseAsync(McpToolInfo tool, System.Text.Json.Nodes.JsonObject arguments, CancellationToken ct)
            => Task.FromResult(new McpCallResult(false, "mcp_disposed", string.Empty, "source_disposed", false, 0, 0));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
