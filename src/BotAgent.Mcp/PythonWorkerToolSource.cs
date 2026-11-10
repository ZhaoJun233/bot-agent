using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Mcp;

namespace BotAgent.Mcp;

/// <summary>Explicitly activated Python tools using the same IMcpToolSource gate as remote tools.
/// Construction performs no I/O. Discovery starts a worker; calls never restart or replay it.
/// This process boundary isolates crashes, not filesystem/network privileges.</summary>
public sealed class PythonWorkerToolSource : IMcpToolSource
{
    private readonly PythonWorkerOptions _options;
    private readonly HashSet<string> _allowed;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Queue<long> _starts = new();
    private PythonWorkerProcess? _worker;
    private McpToolInfo[] _tools = Array.Empty<McpToolInfo>();
    private volatile McpConnectionState _state = McpConnectionState.Disconnected;
    private int _disposed;
    private long _generation;
    private readonly TimeSpan _timeout;
    private readonly int _maxResultBytes;

    public PythonWorkerToolSource(PythonWorkerOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.AllowedTools);
        ArgumentNullException.ThrowIfNull(options.Configuration);
        ArgumentNullException.ThrowIfNull(options.GrantedCapabilities);
        if (!PythonWorkerValidation.IsIdentifier(options.Id)
            || options.AllowedTools.Count > McpLimits.MaxTools
            || options.AllowedTools.Any(t => !PythonWorkerValidation.IsIdentifier(t))
            || options.AllowedTools.Distinct(StringComparer.Ordinal).Count() != options.AllowedTools.Count
            || options.MaxStartsPerMinute is < 1 or > 10)
            throw new ArgumentException("Invalid Python worker options.", nameof(options));
        _options = options with { AllowedTools = options.AllowedTools.ToArray(),
            Configuration = (JsonObject)options.Configuration.DeepClone(),
            GrantedCapabilities = options.GrantedCapabilities.ToArray() };
        _allowed = new HashSet<string>(_options.AllowedTools, StringComparer.Ordinal);
        _log = log;
        _timeout = options.CallTimeout > TimeSpan.Zero && options.CallTimeout <= TimeSpan.FromSeconds(120)
            ? options.CallTimeout : TimeSpan.FromSeconds(McpLimits.DefaultCallTimeoutSeconds);
        _maxResultBytes = Math.Clamp(options.MaxResultBytes, 1, McpLimits.MaxResultBytes);
        if (_allowed.Count == 0) _state = McpConnectionState.Disabled;
    }

    public bool Enabled => Volatile.Read(ref _disposed) == 0 && _allowed.Count != 0;
    public McpConnectionState State => _state;
    public long Generation => Interlocked.Read(ref _generation);
    public string? LastErrorCode { get; private set; }
    /// <summary>Nonblocking notification only. Tool ServerId also contains the process generation.</summary>
    public event Action? GenerationChanged;

    public async Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(CancellationToken ct)
    {
        if (!Enabled) return Array.Empty<McpToolInfo>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        timeout.CancelAfter(_timeout);
        var entered = false;
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (!Enabled) return Array.Empty<McpToolInfo>();
            if (_worker?.Alive == true) return Snapshot();
            if (_worker is not null) await FaultAsync("python_worker_exited").ConfigureAwait(false);
            var now = Stopwatch.GetTimestamp();
            while (_starts.Count > 0 && Stopwatch.GetElapsedTime(_starts.Peek(), now) >= TimeSpan.FromMinutes(1)) _starts.Dequeue();
            if (_starts.Count >= _options.MaxStartsPerMinute) throw new PythonWorkerException("python_restart_budget_exhausted");
            _starts.Enqueue(now);
            _state = McpConnectionState.Connecting;
            var entryPath = PythonWorkerValidation.EntryPath(_options);
            var readiness = await PythonWorkerInterpreter.CheckAsync(_options.ExecutablePath, _options.RequiredPythonVersion, timeout.Token).ConfigureAwait(false);
            if (!readiness.Compatible) throw new PythonWorkerException(readiness.ReasonCode);
            _worker = new PythonWorkerProcess(_options, entryPath, timeout.Token);
            AdvanceGeneration();
            var initialized = await _worker.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["configuration"] = _options.Configuration.DeepClone(),
                ["capabilities"] = new JsonArray(_options.GrantedCapabilities.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            }, timeout.Token).ConfigureAwait(false);
            if (initialized is not JsonObject init || init["protocolVersion"]?.GetValue<int>() != 1)
                throw new PythonWorkerException("python_protocol_version_mismatch");
            var listed = await _worker.RequestAsync("tools/list", new JsonObject(), timeout.Token).ConfigureAwait(false);
            _tools = ParseTools(listed);
            _state = McpConnectionState.Ready;
            LastErrorCode = null;
            Log("python_ready", _tools.Length);
            return Snapshot();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (entered) await FaultAsync("python_cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            if (entered) await FaultAsync(SafeCode(ex)).ConfigureAwait(false);
            return Array.Empty<McpToolInfo>();
        }
        finally { if (entered) _gate.Release(); }
    }

    public Task<McpCallResult> CallAsync(string qualifiedName, JsonObject arguments, CancellationToken ct)
        => CallCoreAsync(qualifiedName, arguments, null, ct);

    /// <summary>Validate the admitted process generation under the same gate as dispatch.</summary>
    public Task<McpCallResult> CallWithGenerationAsync(string qualifiedName, JsonObject arguments, long expectedGeneration, CancellationToken ct)
        => CallCoreAsync(qualifiedName, arguments, expectedGeneration, ct);

    private async Task<McpCallResult> CallCoreAsync(string qualifiedName, JsonObject arguments, long? expectedGeneration, CancellationToken ct)
    {
        if (!Enabled) return new(false, "python_disabled", null);
        var prefix = "mcp." + _options.Id + ".";
        if (!qualifiedName.StartsWith(prefix, StringComparison.Ordinal) || !_allowed.Contains(qualifiedName[prefix.Length..]))
            return new(false, "mcp_not_authorized", null);
        if (Encoding.UTF8.GetByteCount(arguments.ToJsonString()) > McpLimits.MaxArgumentsBytes)
            return new(false, "mcp_arguments_too_large", null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        timeout.CancelAfter(_timeout);
        var entered = false;
        var attempted = false;
        var started = Stopwatch.GetTimestamp();
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (!Enabled) return new(false, "python_disabled", null);
            if (expectedGeneration.HasValue && expectedGeneration.Value != Generation)
                return new(false, "mcp_source_changed", null);
            if (!_tools.Any(t => t.QualifiedName == qualifiedName)) return new(false, "mcp_not_discovered", null);
            if (_worker?.Alive != true) throw new PythonWorkerException("python_worker_exited");
            timeout.Token.ThrowIfCancellationRequested();
            attempted = true;
            var result = await _worker.RequestAsync("tools/call", new JsonObject
            {
                ["name"] = qualifiedName[prefix.Length..], ["arguments"] = arguments.DeepClone(),
            }, timeout.Token).ConfigureAwait(false);
            if (result is not JsonObject value || value["content"] is not JsonValue contentValue
                || !contentValue.TryGetValue<string>(out var content)
                || value["isError"] is not JsonValue errorValue || !errorValue.TryGetValue<bool>(out var isError))
                throw new PythonWorkerException("python_protocol_error", true);
            var size = Encoding.UTF8.GetByteCount(content);
            if (size > _maxResultBytes) return new(false, "mcp_result_too_large", null, UnknownOutcome: true, SizeBytes: size);
            var duration = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // Worker exceptions are intentionally not exposed through content or audit.
            return new(!isError, isError ? "python_tool_error" : "mcp_ok", isError ? null : content,
                $"server={_options.Id} state={(isError ? "error" : "ok")} bytes={size} ms={duration}",
                SizeBytes: size, DurationMs: duration);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (entered) await FaultAsync("python_cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var code = _shutdown.IsCancellationRequested ? "python_disposed" : SafeCode(ex);
            if (entered && ex is not PythonWorkerException { Code: "python_remote_error" }) await FaultAsync(code).ConfigureAwait(false);
            var unknown = attempted && (ex is not PythonWorkerException workerError || workerError.UnknownOutcome);
            return new(false, code, null, UnknownOutcome: unknown);
        }
        finally { if (entered) _gate.Release(); }
    }

    private McpToolInfo[] ParseTools(JsonNode? result)
    {
        if (result is not JsonObject obj || obj["tools"] is not JsonArray tools || tools.Count > McpLimits.MaxTools)
            throw new PythonWorkerException("python_invalid_tools");
        var found = new List<McpToolInfo>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in tools)
        {
            if (node is not JsonObject tool) throw new PythonWorkerException("python_invalid_tools");
            var name = tool["name"]?.GetValue<string>();
            if (!PythonWorkerValidation.IsIdentifier(name) || !names.Add(name!)) throw new PythonWorkerException("python_invalid_tools");
            if (!_allowed.Contains(name!)) continue;
            var description = tool["description"]?.GetValue<string>() ?? "";
            var schema = tool["inputSchema"] as JsonObject;
            if (description.Length > McpLimits.MaxDescriptionLength || schema?["type"]?.GetValue<string>() != "object"
                || Encoding.UTF8.GetByteCount(schema.ToJsonString()) > McpLimits.MaxSchemaBytes)
                throw new PythonWorkerException("python_invalid_tools");
            // Hints are metadata only. Permission/approval still belongs to the host gate.
            var readOnly = (tool["annotations"] as JsonObject)?["readOnlyHint"]?.GetValue<bool>() ?? false;
            found.Add(new(name!, "mcp." + _options.Id + "." + name, description, schema.ToJsonString(),
                _options.Id + "_g" + Generation, readOnly, IsLocalProcess: true,
                SourceGeneration: Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return found.ToArray();
    }

    private IReadOnlyList<McpToolInfo> Snapshot() => _tools.ToArray();
    private static string SafeCode(Exception ex) => ex switch
    {
        PythonWorkerException error => error.Code,
        OperationCanceledException => "mcp_timeout",
        JsonException or InvalidOperationException or FormatException => "python_protocol_error",
        _ => "python_start_failed",
    };

    private void AdvanceGeneration()
    {
        Interlocked.Increment(ref _generation);
        try { GenerationChanged?.Invoke(); } catch { /* Host observation must not break cleanup. */ }
    }

    private void Log(string code, int count = 0)
    {
        try { _log?.Invoke($"[python] id={_options.Id} code={code} tools={count}"); } catch { }
    }

    private async Task FaultAsync(string code)
    {
        LastErrorCode = code;
        var worker = _worker;
        _worker = null;
        _tools = Array.Empty<McpToolInfo>();
        _state = Volatile.Read(ref _disposed) != 0 ? McpConnectionState.Disposed : McpConnectionState.Faulted;
        AdvanceGeneration();
        if (worker is not null) await worker.DisposeAsync().ConfigureAwait(false);
        Log(code);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        // Cancellation interrupts in-flight I/O before taking the serialization gate.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_worker?.Alive == true)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _worker.RequestAsync("shutdown", new JsonObject(), deadline.Token).ConfigureAwait(false); }
                catch { /* A wedged/untrusted unload hook must never block host shutdown. */ }
            }
            await FaultAsync("python_disposed").ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
