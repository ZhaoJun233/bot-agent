using System.Net;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Reply;
using BotAgent.Services.Qq;

internal static partial class RuntimeDeliveryChecks
{
    private const int AudioLimit = 15 * 1024 * 1024;

    public static async Task RunTtsDownloadAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        await test("TTS prepares silk query by key and before fragments", async () =>
        {
            foreach (var (url, query) in new[]
            {
                ("http://localhost/speak", "?format=silk"),
                ("https://example.com/speak?text=synthetic#fragment", "?text=synthetic&format=silk"),
                ("https://example.com/speak?otherformat=mp3", "?otherformat=mp3&format=silk"),
                ("https://example.com/speak?format=mp3", "?format=mp3"),
                ("https://example.com/speak?%66ormat=silk", "?format=silk")
            })
            {
                using var f = new Fixture(clock); f.Receive("first");
                using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
                f.Http.DownloadResponse = _ => AudioResponse(new AudioStream(8));
                Require((await f.Gateway.SendVoiceWithReceiptAsync(true, f.Alias, url)).Ok, "valid TTS did not deliver");
                Require(f.Http.Downloads.Single().AbsolutePath == "/speak" && f.Http.Downloads.Single().Query == query, "TTS query changed path or missed format");
                Require(f.Http.Uploads == 1 && f.Http.Messages.Count == 1, "voice did not use one upload and one final send");
            }
        });
        await test("TTS rejects unsafe URL forms before transport", async () =>
        {
            foreach (var url in new[] { "file:///synthetic", "ftp://example.com/voice", "/speak",
                "https://user:synthetic@example.com/speak", "https://example.com/speak?text=a b",
                "https://example.com/speak?text=a\nb", "https://example.com/" + new string('a', 2048) })
            {
                using var f = new Fixture(clock); f.Receive("first");
                using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
                f.Http.DownloadResponse = _ => AudioResponse(new AudioStream(8));
                var result = await f.Gateway.SendVoiceWithReceiptAsync(true, f.Alias, url);
                Require(!result.Ok && result.Delivery?.ReasonCode == "audio_url_invalid" && f.Http.Downloads.Count == 0
                    && f.Http.Uploads == 0, "invalid URL reached transport or lost reason");
            }
        });
        await test("TTS declared oversize does not read the response body", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first");
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            var stream = new AudioStream(AudioLimit + 8192);
            f.Http.DownloadResponse = _ => AudioResponse(stream, AudioLimit + 8192);
            var result = await f.Gateway.SendVoiceWithReceiptAsync(true, f.Alias, "https://example.com/speak?text=synthetic");
            Require(!result.Ok && result.Delivery?.ReasonCode == "media_size_invalid" && stream.ReadBytes == 0
                && stream.Disposed && f.Http.Uploads == 0, "declared oversize was buffered or uploaded");
        });
        foreach (var declared in new long?[] { null, 1 })
        {
            await test("TTS bounds streamed size with " + (declared is null ? "absent" : "false") + " Content-Length", async () =>
            {
                using var f = new Fixture(clock); f.Receive("first");
                using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
                var stream = new AudioStream(AudioLimit + 8192);
                f.Http.DownloadResponse = _ => AudioResponse(stream, declared);
                var result = await f.Gateway.SendVoiceWithReceiptAsync(true, f.Alias, "https://example.com/speak?text=synthetic");
                Require(!result.Ok && result.Delivery?.ReasonCode == "media_size_invalid" && stream.ReadBytes == AudioLimit + 1
                    && stream.Disposed && f.Http.Uploads == 0 && f.Http.Messages.Count == 0, "oversize was fully read or reached official API");
            });
        }
        await test("TTS accepts exactly the upload size limit", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first");
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            var stream = new AudioStream(AudioLimit);
            f.Http.DownloadResponse = _ => AudioResponse(stream);
            var written = 0L;
            f.Http.ReadUpload = async (content, ct) =>
            {
                using var sink = new UploadSink();
                await content.CopyToAsync(sink, ct);
                written = sink.Bytes;
                Require(content.Headers.ContentLength == written, "upload length does not match serialized body");
            };
            var result = await f.Gateway.SendVoiceWithReceiptAsync(true, f.Alias, "https://example.com/speak?text=synthetic");
            Require(result.Ok && stream.ReadBytes == AudioLimit && stream.Disposed && f.Http.Uploads == 1
                && written > AudioLimit * 4L / 3 && f.Http.Messages.Single()["msg_id"]!.GetValue<string>() == "first",
                "limit boundary or trigger binding changed");
        });
        await test("official media upload preserves binary data across Base64 boundaries", async () =>
        {
            foreach (var length in new[] { 1, 2, 3, 32765, 32766, 32767, 65533 })
            {
                using var f = new Fixture(clock); f.Receive("first");
                using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
                var data = new byte[length]; new Random(length).NextBytes(data);
                var reads = 0;
                f.Http.UploadResponse = count => count == 1 ? Reply(401, "{\"code\":11244}")
                    : Reply(200, "{\"file_info\":\"synthetic-media\",\"ttl\":60}");
                f.Http.ReadUpload = async (content, ct) =>
                {
                    var body = System.Text.Json.Nodes.JsonNode.Parse(await content.ReadAsStringAsync(ct))!;
                    Require(body["file_type"]!.GetValue<int>() == 1 && !body["srv_send_msg"]!.GetValue<bool>()
                        && Convert.FromBase64String(body["file_data"]!.GetValue<string>()).SequenceEqual(data),
                        "serialized upload changed fields or binary data");
                    reads++;
                };
                Require(await f.Gateway.SendImageAsync(true, f.Alias, data) && reads == 2 && f.Http.Messages.Count == 1,
                    "credential retry did not preserve upload bytes or repeated message");
            }
        });
        await test("official upload cancellation retains Unknown without a final message", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first");
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            using var cancel = new CancellationTokenSource();
            f.Http.ReadUpload = async (content, ct) =>
            {
                using var sink = new UploadSink { AfterWrite = cancel.Cancel };
                await content.CopyToAsync(sink, ct);
                ct.ThrowIfCancellationRequested();
            };
            var result = await f.Gateway.SendImageWithReceiptAsync(true, f.Alias, new byte[65533], cancel.Token);
            Require(result.Delivery?.Status == BotAgent.Domain.Platforms.DeliveryStatus.Unknown
                && f.Http.Uploads == 1 && f.Http.Messages.Count == 0, "cancelled upload was retried or declared delivered");
        });
        await test("TTS HTTP rejection and empty body do not upload", async () =>
        {
            foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.OK })
            {
                using var f = new Fixture(clock); f.Receive("first");
                using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
                var stream = new AudioStream(status == HttpStatusCode.OK ? 0 : 8192);
                f.Http.DownloadResponse = _ => new(status) { Content = new StreamContent(stream) };
                Require(!(await f.Gateway.SendVoiceWithReceiptAsync(true, f.Alias, "https://example.com/speak?text=synthetic")).Ok
                    && stream.ReadBytes == 0 && stream.Disposed && f.Http.Uploads == 0, "rejected or empty audio was consumed or uploaded");
            }
        });
        await test("TTS response body remains inside the download deadline", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first");
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            using var cleanup = new CancellationTokenSource();
            var stream = new AudioStream(8) { WaitForCancellation = true };
            f.Http.Timeout = TimeSpan.FromMilliseconds(75);
            f.Http.DownloadResponse = _ => AudioResponse(stream);
            var pending = f.Gateway.SendVoiceWithReceiptAsync(true, f.Alias, "https://example.com/speak?text=synthetic", cleanup.Token);
            var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(2))) == pending;
            if (!completed) cleanup.Cancel();
            var result = await pending;
            Require(completed && result.Delivery?.ReasonCode == "audio_download_timeout" && stream.Disposed
                && f.Http.Uploads == 0, "response body escaped timeout or lost timeout reason");
        });
        await test("TTS caller cancellation is preserved without upload", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first");
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            using var cancel = new CancellationTokenSource();
            var stream = new AudioStream(8) { WaitForCancellation = true };
            f.Http.DownloadResponse = _ => AudioResponse(stream);
            var pending = f.Gateway.SendVoiceWithReceiptAsync(true, f.Alias, "https://example.com/speak?text=synthetic", cancel.Token);
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
            var cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && stream.Disposed && f.Http.Uploads == 0 && f.Http.Messages.Count == 0, "cancellation was swallowed or sent audio");
        });
        await test("TTS retired source cannot upload downloaded audio", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first");
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.Http.DownloadResponse = _ => AudioResponse(new AudioStream(8) { BeforeRead = () => f.Current = false });
            Require(!(await f.Gateway.SendVoiceWithReceiptAsync(true, f.Alias, "https://example.com/speak?text=synthetic")).Ok
                && f.Http.Uploads == 0 && f.Http.Messages.Count == 0, "retired source uploaded audio");
        });
        await test("TTS stream failures retain only safe diagnostics", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first");
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            var stream = new AudioStream(8) { BeforeRead = () => throw new IOException("synthetic-private-value") };
            f.Http.DownloadResponse = _ => AudioResponse(stream);
            var result = await f.Gateway.SendVoiceWithReceiptAsync(true, f.Alias, "https://example.com/speak?text=synthetic-private-value");
            Require(!result.Ok && result.Delivery?.ReasonCode == "audio_unavailable" && stream.Disposed && f.Http.Uploads == 0
                && f.Logs.All(log => !log.Contains("synthetic-private-value")), "stream failure leaked text or reached upload");
        });
    }

    private static HttpResponseMessage AudioResponse(AudioStream stream, long? declaredLength = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        if (declaredLength is not null) response.Content.Headers.ContentLength = declaredLength;
        return response;
    }

    private sealed class UploadSink : Stream
    {
        public long Bytes { get; private set; }
        public Action? AfterWrite { get; init; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { Bytes += count; AfterWrite?.Invoke(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Bytes += buffer.Length; AfterWrite?.Invoke(); return ValueTask.CompletedTask; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class AudioStream(int length) : Stream
    {
        public int ReadBytes { get; private set; }
        public bool Disposed { get; private set; }
        public bool WaitForCancellation { get; init; }
        public Action? BeforeRead { get; init; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ReadStarted.TrySetResult(); BeforeRead?.Invoke(); ct.ThrowIfCancellationRequested();
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var count = Math.Min(buffer.Length, length - ReadBytes);
            buffer.Span[..count].Clear(); ReadBytes += count; return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
