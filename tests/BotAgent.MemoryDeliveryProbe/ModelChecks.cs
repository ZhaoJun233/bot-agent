using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Model;
using BotAgent.Services.Memory;


internal static partial class Probe
{
    private static ModelTransport Transport(FakeHttp http)
    {
        var options = new ModelOptions
        {
            ApiKey = "synthetic-key",
            BaseUrl = "https://example.com/v1",
            Model = "synthetic-model"
        };
        return new ModelTransport(() => options, http, http, new ImageDownloader(http), TimeSpan.FromSeconds(3));
    }

    private static JsonObject Payload() => new()
    {
        ["model"] = "synthetic-model",
        ["reasoning_effort"] = "high",
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "synthetic-memory-derived-input" })
    };

    private static async Task ModelChecks()
    {
        await Run("model.no-memory-request-unchanged", async () =>
        {
            var http = new FakeHttp();
            await Transport(http).SendAsync(Payload(), 0, Array.Empty<long>(), CancellationToken.None);
            Require(http.Sends == 1);
        });
        foreach (var reason in new[] { "revoke", "forget", "edit", "disable" })
        {
            await Run("model." + reason + "-blocks-next-call", async () =>
            {
                using var scope = MemoryUseScope.Begin();
                var receipt = new MemoryGate();
                MemoryUseScope.Register(receipt.TryUse);
                var http = new FakeHttp();
                var model = Transport(http);
                await model.SendAsync(Payload(), 0, Array.Empty<long>(), CancellationToken.None);
                receipt.Invalidate();
                await IgnoreDenial(() => model.SendAsync(Payload(), 0, Array.Empty<long>(), CancellationToken.None));
                Require(http.Sends == 1);
            });
        }
        await Run("model.invalidated-after-first-attempt-blocks-fallback-retry", async () =>
        {
            using var scope = MemoryUseScope.Begin();
            var receipt = new MemoryGate();
            MemoryUseScope.Register(receipt.TryUse);
            var http = new FakeHttp
            {
                OnSend = _ =>
                {
                    receipt.Invalidate();
                    return Task.FromResult(FakeHttp.Response("{\"error\":{\"message\":\"Unsupported reasoning effort\",\"param\":\"store\"}}", HttpStatusCode.BadRequest));
                }
            };
            await IgnoreDenial(() => Transport(http).SendAsync(Payload(), 0, Array.Empty<long>(), CancellationToken.None));
            Require(http.Sends == 1);
        });
        await Run("model.inflight-generation-may-finish-but-output-cannot-commit", async () =>
        {
            using var scope = MemoryUseScope.Begin();
            var receipt = new MemoryGate();
            MemoryUseScope.Register(receipt.TryUse);
            var started = Signal();
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var http = new FakeHttp { OnSend = _ => { started.SetResult(); return response.Task; } };
            var generation = Transport(http).SendAsync(Payload(), 0, Array.Empty<long>(), CancellationToken.None);
            await started.Task;
            receipt.Invalidate();
            response.SetResult(FakeHttp.Response());
            await generation;
            var delivered = 0;
            Require(!MemoryUseScope.TryUse(() => delivered++) && delivered == 0);
        });
        await Run("model.agent-direct-chat-rechecks-receipt", async () =>
        {
            using var scope = MemoryUseScope.Begin();
            var receipt = new MemoryGate();
            MemoryUseScope.Register(receipt.TryUse);
            var http = new FakeHttp();
            var options = new ModelOptions { ApiKey = "synthetic-key", BaseUrl = "https://example.com/v1", Model = "synthetic-model" };
            var client = new OpenAiClient(() => options, http, http, new ImageDownloader(http), Transport(http));
            await client.CompleteChatWithReasoningAsync("synthetic-model", "synthetic-system", [("user", "synthetic-text")], 128, 0, null);
            receipt.Invalidate();
            await IgnoreDenial(() => client.CompleteChatWithReasoningAsync("synthetic-model", "synthetic-system", [("user", "synthetic-text")], 128, 0, null));
            Require(http.Sends == 1);
        });
        await Run("model.auxiliary-curation-and-description-block-invalid-input", async () =>
        {
            using var scope = MemoryUseScope.Begin();
            var receipt = new MemoryGate();
            MemoryUseScope.Register(receipt.TryUse);
            var http = new FakeHttp();
            var options = new ModelOptions { ApiKey = "synthetic-key", BaseUrl = "https://example.com/v1", Model = "synthetic-model" };
            var client = new OpenAiClient(() => options, http, http, new ImageDownloader(http), Transport(http));
            receipt.Invalidate();
            await client.CurateStickersAsync("synthetic-description", 1, CancellationToken.None);
            await client.DescribeStickerAsync(new byte[] { 1, 2, 3 }, "image/png", CancellationToken.None);
            Require(http.Sends == 0);
        });
    }

    private sealed class FakeHttp : BotAgent.Model.Net.IModelHttpFetcher, BotAgent.Platforms.Net.IHttpFetcher
    {
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
        public int Sends;
        public int Posts;
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnSend;
        public Func<string, Task<HttpResponseMessage>>? OnPost;
        public static HttpResponseMessage Response(string body = "{\"choices\":[{\"message\":{\"content\":\"synthetic-response\"},\"finish_reason\":\"stop\"}]}", HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(body) };
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Sends);
            return OnSend?.Invoke(request) ?? Task.FromResult(Response());
        }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken ct = default)
            => SendAsync(request, ct);
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Posts);
            return OnPost?.Invoke(url) ?? Task.FromResult(Response("{\"access_token\":\"synthetic-token\",\"expires_in\":3600,\"code\":0,\"tenant_access_token\":\"synthetic-token\",\"expire\":3600}"));
        }
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption completionOption, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption completionOption, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
