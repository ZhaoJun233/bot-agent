using System.Net;
using BotAgent.Adapters.Net;
using BotAgent.Plugins.Net;

internal static class WebPageTransportChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var content = new ObservedContent(new MemoryStream(new byte[100000]));
        var http = new SyntheticHttp(request => request.RequestUri!.AbsolutePath == "/start"
            ? SyntheticHttp.Redirect("https://cdn.example.com/final")
            : new(HttpStatusCode.OK) { Content = content });
        var pages = new WebPageTransport(http, () => false);
        check(!pages.TryValidate("http://localhost/private", out _, out _), "public page contract uses host URL policy");
        var result = await pages.GetAsync(new(new Uri("https://example.com/start"), 33000, "Synthetic", "en"), default);
        check(result.Body.Length == 65536 && result.Url.Host == "cdn.example.com" && result.StatusCode == 200,
            "page contract preserves 32-KiB block cutoff and final address");
        check(content.Disposed && http.Authorization.All(value => value is null), "page response released before materialized bytes return; no credentials");
        var fullContent = new ObservedContent(new MemoryStream(new byte[100000]));
        var full = new WebPageTransport(new SyntheticHttp(_ => new(HttpStatusCode.OK) { Content = fullContent }), () => false);
        check((await full.GetAsync(new(new Uri("https://example.com/all")), default)).Body.Length == 100000 && fullContent.Disposed,
            "complete-source reads preserve existing unbounded semantics and release response");
        var failedStream = new PendingBody { Fail = true };
        var failedContent = new ObservedContent(failedStream);
        var status = new WebPageTransport(new SyntheticHttp(_ => new(HttpStatusCode.BadGateway) { Content = failedContent }), () => false);
        var failed = await status.GetAsync(new(new Uri("https://example.com/status")), default);
        check(failed.StatusCode == 502 && failed.Body.Length == 0 && failedContent.Disposed && !failedStream.Entered.Task.IsCompleted,
            "non-success response is released without reading its body");
        foreach (var cancel in new[] { false, true })
        {
            var body = new PendingBody { Fail = !cancel };
            var observed = new ObservedContent(body);
            var transport = new WebPageTransport(new SyntheticHttp(_ => new(HttpStatusCode.OK) { Content = observed }), () => false);
            using var cts = new CancellationTokenSource();
            var pending = transport.GetAsync(new(new Uri("https://example.com/body"), 256 * 1024), cts.Token);
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel)
            {
                check(!pending.IsCompleted && !observed.Disposed, "response stays owned while the body read is pending");
                cts.Cancel();
            }
            var refused = false;
            try { await pending; }
            catch (OperationCanceledException) when (cancel) { refused = true; }
            catch (IOException) when (!cancel) { refused = true; }
            check(refused && observed.Disposed, cancel ? "body cancellation propagates and releases response" : "body failure propagates and releases response");
        }
        var unused = new SyntheticHttp(_ => SyntheticHttp.Image());
        var invalid = new WebPageTransport(unused, () => false);
        var rejected = false;
        try { await invalid.GetAsync(new(new Uri("https://example.com/invalid"), 0), default); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected && unused.Urls.Count == 0, "invalid read boundary performs no I/O");
    }

    private sealed class ObservedContent(Stream stream) : StreamContent(stream)
    {
        public bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class PendingBody : Stream
    {
        public bool Fail;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            if (Fail) throw new IOException("synthetic_body_failure");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
