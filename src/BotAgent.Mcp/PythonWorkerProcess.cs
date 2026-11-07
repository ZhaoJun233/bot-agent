using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotAgent.Mcp;

/// <summary>One serialized NDJSON channel; never logs worker-controlled bytes.</summary>
internal sealed class PythonWorkerProcess : IAsyncDisposable
{
    private const int MaxFrameBytes = 512 * 1024;
    private readonly Process _process;
    private readonly Task _stderrDrain = Task.CompletedTask;
    private readonly Task _parentExit = Task.CompletedTask;
    private readonly PythonWorkerProcessOwnership _ownership;
    private readonly byte[] _readBuffer = new byte[4096];
    private int _readOffset;
    private int _readLength;
    private long _sequence;
    private long _stderrBytes;
    private bool _disposed;
    public long StderrBytes => Interlocked.Read(ref _stderrBytes);
    public bool Alive => !_disposed && !_process.HasExited;

    public PythonWorkerProcess(PythonWorkerOptions options, string entryPath, CancellationToken ct = default)
    {
        var start = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetFullPath(options.PackageDirectory),
        };
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        // -I isolates user site packages and PYTHON* env. The package itself is explicitly trusted.
        foreach (var arg in new[] { "-I", "-X", "utf8", "-u", "-c",
            "import os,runpy,sys; os.name == 'posix' and os.setsid(); " +
            "sys.stdout.buffer.write(b'BOTAGENT_OWNED\\n'); sys.stdout.buffer.flush(); " +
            "sys.stdin.buffer.read(3) == b'GO\\n' or sys.exit(125); " +
            "sys.path.insert(0,sys.argv[2]); runpy.run_path(sys.argv[1],run_name='__main__')",
            entryPath, Path.GetFullPath(options.PackageDirectory) }) start.ArgumentList.Add(arg);
        _process = new Process { StartInfo = start };
        try { _ownership = PythonWorkerProcessOwnership.Create(); }
        catch { _process.Dispose(); throw new PythonWorkerException("python_ownership_failed"); }
        var started = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!_process.Start()) throw new PythonWorkerException("python_start_failed");
            started = true;
            _stderrDrain = DrainStderrAsync();
            AdmitOwnedProcess(options.CallTimeout, ct);
            _parentExit = ReapDescendantsOnExitAsync();
        }
        catch
        {
            _ownership.Dispose();
            if (started)
            {
                try { _process.Kill(entireProcessTree: true); _process.WaitForExit(3000); } catch { }
            }
            _process.Dispose();
            ct.ThrowIfCancellationRequested();
            throw new PythonWorkerException(started ? "python_ownership_failed" : "python_start_failed");
        }
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = ++_sequence;
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
        var bytes = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
        if (bytes.Length > MaxFrameBytes) throw new PythonWorkerException("python_request_too_large");
        var sent = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            // A partial write already makes the outcome unknown; never retry a call automatically.
            sent = true;
            await _process.StandardInput.BaseStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
            var responseBytes = await ReadFrameAsync(ct).ConfigureAwait(false);
            if (StderrBytes > 64 * 1024) throw new PythonWorkerException("python_stderr_limit", true);
            // Only one request is outstanding. Buffered extra bytes are an unsolicited/duplicate frame.
            if (_readOffset < _readLength) throw new PythonWorkerException("python_protocol_error", true);
            JsonObject response;
            try
            {
                response = JsonNode.Parse(responseBytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject
                    ?? throw new JsonException();
                if (response["jsonrpc"]?.GetValue<string>() != "2.0" || response["id"]?.GetValue<long>() != id
                    || response.ContainsKey("result") == response.ContainsKey("error")) throw new JsonException();
                if (response.ContainsKey("error") && (response["error"] is not JsonObject error ||
                    error["code"] is not JsonValue code || !code.TryGetValue<int>(out _) ||
                    error["message"] is not JsonValue message || !message.TryGetValue<string>(out _)))
                    throw new JsonException();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            { throw new PythonWorkerException("python_protocol_error", true); }
            if (response.ContainsKey("error"))
                throw new PythonWorkerException("python_remote_error", method == "tools/call");
            return response["result"]?.DeepClone();
        }
        catch (PythonWorkerException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        { throw new PythonWorkerException("python_worker_exited", sent); }
    }

    private void AdmitOwnedProcess(TimeSpan configuredTimeout, CancellationToken ct)
    {
        // No package code runs until the host has established OS descendant ownership.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(configuredTimeout > TimeSpan.Zero && configuredTimeout <= TimeSpan.FromSeconds(120)
            ? configuredTimeout : TimeSpan.FromSeconds(20));
        var expected = "BOTAGENT_OWNED\n"u8.ToArray();
        var hello = new byte[expected.Length];
        _process.StandardOutput.BaseStream.ReadExactlyAsync(hello, timeout.Token).AsTask().GetAwaiter().GetResult();
        if (!hello.AsSpan().SequenceEqual(expected)) throw new PythonWorkerException("python_ownership_failed");
        _ownership.Attach(_process);
        _process.StandardInput.BaseStream.Write("GO\n"u8);
        _process.StandardInput.BaseStream.Flush();
    }

    private async Task ReapDescendantsOnExitAsync()
    {
        try { await _process.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        finally { _ownership.Terminate(); }
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        using var frame = new MemoryStream();
        while (true)
        {
            if (_readOffset == _readLength)
            {
                _readLength = await _process.StandardOutput.BaseStream.ReadAsync(_readBuffer, ct).ConfigureAwait(false);
                _readOffset = 0;
                if (_readLength == 0) throw new PythonWorkerException("python_worker_exited", true);
            }
            var newline = Array.IndexOf(_readBuffer, (byte)'\n', _readOffset, _readLength - _readOffset);
            var count = (newline < 0 ? _readLength : newline) - _readOffset;
            if (frame.Length + count > MaxFrameBytes) throw new PythonWorkerException("python_frame_too_large", true);
            frame.Write(_readBuffer, _readOffset, count);
            _readOffset += count;
            if (newline >= 0) { _readOffset++; return frame.ToArray(); }
        }
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new byte[4096];
        try
        {
            int count;
            while ((count = await _process.StandardError.BaseStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (Interlocked.Add(ref _stderrBytes, count) <= 64 * 1024) continue;
                _ownership.Terminate();
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void Kill()
    {
        _ownership.Terminate();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
        try { await _parentExit.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        _process.Dispose();
        try { await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}
