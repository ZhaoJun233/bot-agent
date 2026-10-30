using System.Net;
using System.Text;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private long _sseAuthGeneration;

    private Func<bool> SseAuthorization(HttpListenerContext context)
    {
        lock (_loginGate)
        {
            var generation = Interlocked.Read(ref _sseAuthGeneration);
            if (!IsAuthorized(context)) throw new UnauthorizedAccessException("sse_session_revoked");
            return () =>
            {
                lock (_loginGate)
                    return generation == Interlocked.Read(ref _sseAuthGeneration) && IsAuthorized(context);
            };
        }
    }

    private void DisconnectSseClients()
    {
        List<SseClient> clients;
        lock (_clientsGate) { clients = _clients.ToList(); _clients.Clear(); }
        foreach (var client in clients) client.Dispose();
    }

    /// <summary>Every queued frame rechecks its originating authorization, not the newest login.</summary>
    private sealed class SseClient : IDisposable
    {
        private readonly Stream _stream;
        private readonly Func<bool> _authorized;
        private readonly object _authorizationGate;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly CancellationTokenSource _closed = new();
        private int _disposed;

        public SseClient(Stream stream, Func<bool> authorized) : this(stream, authorized, new object()) { }
        public SseClient(Stream stream, Func<bool> authorized, object authorizationGate)
        { _stream = stream; _authorized = authorized; _authorizationGate = authorizationGate; }
        public bool IsAuthorized => Volatile.Read(ref _disposed) == 0 && _authorized();
        public Task SendAsync(string @event, string data) => WriteRawAsync($"event: {@event}\ndata: {data}\n\n");
        public Task SendCommentAsync(string comment) => WriteRawAsync($": {comment}\n\n");

        private async Task WriteRawAsync(string payload)
        {
            await _writeGate.WaitAsync(_closed.Token);
            try
            {
                ValueTask write;
                lock (_authorizationGate)
                {
                    if (!IsAuthorized) { Dispose(); throw new UnauthorizedAccessException("sse_session_revoked"); }
                    write = _stream.WriteAsync(Encoding.UTF8.GetBytes(payload), _closed.Token);
                }
                await write;
                Task flush;
                lock (_authorizationGate)
                {
                    if (!IsAuthorized) { Dispose(); throw new UnauthorizedAccessException("sse_session_revoked"); }
                    flush = _stream.FlushAsync(_closed.Token);
                }
                await flush;
            }
            finally { _writeGate.Release(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _closed.Cancel();
            try { _stream.Dispose(); } catch { /* disconnected */ }
            // Writers may still release the gate; disposing it here races in-flight frames.
        }
    }
}
