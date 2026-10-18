extern alias ModelModule;

using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Model;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using ModelClient = ModelModule::BotAgent.Model.OpenAiClient;
using ModelTransport = ModelModule::BotAgent.Model.ModelTransport;
using ModelImages = ModelModule::BotAgent.Model.ImageDownloader;
using ModelOptions = ModelModule::BotAgent.Model.ModelOptions;
using ModelFault = ModelModule::BotAgent.Model.ModelFault;
using ModelLogging = ModelModule::BotAgent.Services.ModelLogging;
using Fetcher = ModelModule::BotAgent.Model.Net.IModelHttpFetcher;
using ConnectionTester = ModelModule::BotAgent.Model.ProviderConnectionTester;

namespace BotAgent.ReviewRemediationProbe;

internal static class ModelMigrationTests
{
    private const string Marker = "synthetic-private-marker";
    private const string Success = "{\"choices\":[{\"message\":{\"content\":\"{\\\"action\\\":\\\"reply\\\",\\\"suitability\\\":99,\\\"reply\\\":\\\"synthetic\\\"}\"}}]}";
    private static readonly ChatMessage[] Window = { new() { Role = MessageRole.Peer, Text = "synthetic", Timestamp = DateTimeOffset.UnixEpoch } };

    internal static async Task<int?> TryRunChildAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--model-migration") return null;
        var output = Console.Out;
        var error = Console.Error;
        using var diagnostics = new StringWriter();
        var results = new List<(bool Passed, string Name)>();
        string? exceptionType = null;
        try
        {
            // The isolated child emits only our fixed check labels, never captured diagnostics.
            Console.SetOut(diagnostics);
            Console.SetError(diagnostics);
            await RunAsync((passed, name) => results.Add((passed, name)));
        }
        catch (Exception ex)
        {
            exceptionType = ex.GetType().Name;
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
        foreach (var result in results)
            output.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Name}");
        if (exceptionType is not null)
            output.WriteLine($"FAIL model migration exception_type={exceptionType}");
        var failures = results.Count(result => !result.Passed) + (exceptionType is null ? 0 : 1);
        output.WriteLine($"model migration checks={results.Count} failures={failures}");
        return failures == 0 ? 0 : 1;
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        ModelModule::BotAgent.Model.ModelClock.Initialize(ClockBindings.Source);
        var originalClock = Clock.Current;
        var originalWrite = ModelLogging.WriteHandler;
        var originalWarn = ModelLogging.WarnHandler;
        var logs = new List<string>();
        Clock.Use(new InstantClock());
        ModelLogging.WriteHandler = (_, message) => logs.Add(message);
        ModelLogging.WarnHandler = (_, message) => logs.Add(message);
        try
        {
            check(ReferenceEquals(ModelModule::BotAgent.Model.ModelClock.Current, ClockBindings.Source)
                && ModelModule::BotAgent.Model.ModelClock.Current.Now == Clock.Now,
                "Model uses the explicitly bound Host clock instance");
            await PayloadAsync(check);
            await FailuresAsync(check);
            await FallbackAsync(check);
            await ImagesAsync(check);
            await ConnectionAsync(check);
            check(logs.All(line => !line.Contains(Marker, StringComparison.Ordinal)
                && !line.Contains("example.com", StringComparison.Ordinal) && !line.Contains("data:image", StringComparison.Ordinal)),
                "Model diagnostics contain no synthetic body credential URL or image payload");
            check(typeof(BotAgent.Services.Agent.OpenAiClient).BaseType == typeof(ModelClient), "Host client delegates to canonical Model implementation");
            var hostAssembly = typeof(BotAgent.Services.Agent.OpenAiClient).Assembly;
            check(hostAssembly.GetType("BotAgent.Adapters.Model.ModelTransport")?.BaseType == typeof(ModelTransport), "Host transport has only the canonical Model algorithm");
            check(hostAssembly.GetType("BotAgent.Adapters.Model.ImageDownloader")?.BaseType == typeof(ModelImages), "Host image downloader delegates to Model");
        }
        finally
        {
            ModelLogging.WriteHandler = originalWrite;
            ModelLogging.WarnHandler = originalWarn;
            Clock.Use(originalClock);
        }
    }

    private static ModelOptions Options() => new() { BaseUrl = "https://example.com/v1", ApiKey = Marker, Model = "synthetic-main", FastModel = "synthetic-fast", EnableAtmosphereDamping = false };
    private static ModelTransport Transport(ModelOptions options, ProbeFetcher fetcher) => new(() => options, fetcher, fetcher, new ModelImages(fetcher), fetcher.Timeout);

    private static async Task PayloadAsync(Action<bool, string> check)
    {
        var options = Options();
        var fetcher = new ProbeFetcher();
        var transport = Transport(options, fetcher);
        foreach (var fast in new[] { false, true })
        foreach (var item in new[] { ("none", "none", 0), ("off", "none", 0), ("disabled", "none", 0),
            ("low", "low", fast ? 1024 : 0), ("medium", "medium", fast ? 4096 : 0),
            ("high", "high", fast ? 16384 : 0), ("xhigh", "xhigh", 0), ("max", "xhigh", 0),
            ("custom", "777", 777), ("invalid", fast ? "low" : "medium", fast ? 1024 : 0) })
        {
            options.FastReply = fast;
            options.ThinkingBudget = options.FastThinkingBudget = item.Item1;
            options.ThinkingCustomBudget = options.FastThinkingCustomBudget = "777";
            var built = await transport.BuildAsync(Window, "synthetic", Array.Empty<long>(), default);
            await transport.SendAsync(built.Payload, 0, Array.Empty<long>(), default);
            var wire = fetcher.Payloads.Last();
            check(wire["model"]!.GetValue<string>() == (fast ? "synthetic-fast" : "synthetic-main"), "actual Model wire preserves main and fast routing");
            check((wire["reasoning_effort"]?.GetValue<string>() == item.Item2 || item.Item1 == "custom" && !wire.ContainsKey("reasoning_effort"))
                && (wire["max_thinking_tokens"]?.GetValue<int>() ?? 0) == item.Item3, "actual Model wire preserves budget and explicit none");
            var host = new AppSettings { ThinkingBudget = item.Item1, FastThinkingBudget = item.Item1, ThinkingCustomBudget = "777", FastThinkingCustomBudget = "777" };
            check(host.ResolveThinkingBudget(fast) == options.ResolveThinkingBudget(fast), "Host and Model use one complete budget mapping");
        }
        foreach (var fast in new[] { false, true })
        foreach (var custom in new[] { "", "invalid", "-1", "0", "999999999999999999999" })
        {
            options.FastReply = fast;
            options.ThinkingBudget = options.FastThinkingBudget = "custom";
            options.ThinkingCustomBudget = options.FastThinkingCustomBudget = custom;
            var built = await transport.BuildAsync(Window, "synthetic", Array.Empty<long>(), default);
            await transport.SendAsync(built.Payload, 0, Array.Empty<long>(), default);
            var wire = fetcher.Payloads.Last();
            check(wire["max_thinking_tokens"]!.GetValue<int>() == (fast ? 1024 : 4096), "actual custom invalid numeric and empty boundaries preserve default budget");
            check(BotAgent.Model.ThinkingBudgetResolver.ResolveBudget("custom", custom, fast) == options.ResolveThinkingBudget(fast), "canonical custom budget mapping remains unique");
        }
        options.Model = "o3-synthetic";
        options.FastReply = false;
        var reasoning = await transport.BuildAsync(Window, "synthetic", Array.Empty<long>(), default);
        check(!reasoning.Payload.ContainsKey("temperature") && !reasoning.Payload.ContainsKey("top_p"), "reasoning model sampling omission unchanged");
        var callerPayload = new JsonObject { ["model"] = "synthetic-original", ["messages"] = new JsonArray() };
        await transport.SendAsync(callerPayload, 0, Array.Empty<long>(), new ModelProviderRoute("synthetic-provider", "https://example.com/registered", "synthetic-routed", Marker), default);
        check(fetcher.Payloads.Last()["model"]!.GetValue<string>() == "synthetic-routed" && callerPayload["model"]!.GetValue<string>() == "synthetic-original", "registered route does not mutate another request");
        options.Model = "synthetic-main";
        var hostClient = new BotAgent.Services.Agent.OpenAiClient(new SettingsBox(new AppSettings { ApiKey = Marker, Model = "synthetic-main", ModelBaseUrl = "https://example.com/v1", EnableAtmosphereDamping = false }), fetcher, fetcher, new ModelImages(fetcher), transport);
        var result = await hostClient.CompleteAsync(Window);
        check(result.Reply == "synthetic", "real Host facade sends and parses a synthetic request through Model");
        check(fetcher.AllDisposed, "every Model payload request and response is disposed");
    }

    private static async Task FailuresAsync(Action<bool, string> check)
    {
        foreach (var item in new[] { (401, "model_auth_failed", 1), (403, "model_auth_failed", 1),
            (429, "model_rate_limited", 2), (503, "model_upstream_error", 2) })
        {
            var http = new ProbeFetcher { Handler = (_, _) => Task.FromResult(Response(item.Item1, Marker)) };
            try { await Transport(Options(), http).SendAsync(new JsonObject(), 0, Array.Empty<long>(), default); check(false, "expected safe status failure"); }
            catch (ModelFault fault) { check(fault.ErrorCode == item.Item2 && (int?)fault.StatusCode == item.Item1 && fault.InnerException is null && !fault.ToString().Contains(Marker), "safe typed status failure"); }
            check(http.Count == item.Item3 && http.AllDisposed, "status retry bounded and response disposed");
        }
        var timeout = new ProbeFetcher { Handler = (_, _) => throw new TaskCanceledException(Marker) };
        try { await Transport(Options(), timeout).SendAsync(new JsonObject(), 0, Array.Empty<long>(), default); check(false, "timeout expected"); }
        catch (ModelFault fault) { check(fault.ErrorCode == "model_timeout" && timeout.Count == 2, "noncaller timeout has one bounded retry"); }
        using var cancel = new CancellationTokenSource();
        var cancelled = new ProbeFetcher { Handler = (_, _) => { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); } };
        try { await Transport(Options(), cancelled).SendAsync(new JsonObject(), 0, Array.Empty<long>(), cancel.Token); check(false, "caller cancellation expected"); }
        catch (OperationCanceledException) { check(cancelled.Count == 1, "caller cancel propagates without retry"); }
        var malformed = new ProbeFetcher { Handler = (_, _) => Task.FromResult(Response(200, Marker)) };
        try { await Transport(Options(), malformed).SendAsync(new JsonObject(), 0, Array.Empty<long>(), default); check(false, "malformed expected"); }
        catch (ModelFault fault) { check(fault.ErrorCode == "model_malformed_response" && !fault.ToString().Contains(Marker), "malformed response is safe and distinct"); }
        var aux = new ModelClient(Options, malformed, malformed, new ModelImages(malformed), Transport(Options(), malformed));
        check(await aux.CompleteChatAsync("synthetic", "synthetic", Array.Empty<(string, string)>(), 64, 0) is null, "nullable auxiliary API retains null on malformed response");
    }

    private static async Task FallbackAsync(Action<bool, string> check)
    {
        var http = new ProbeFetcher { Handler = (count, _) => Task.FromResult(Response(200, count < 3 ? "{\"choices\":[]}" : Success)) };
        var payload = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AAAA" } }, new JsonObject { ["type"] = "text", ["text"] = "synthetic" }) }) };
        var outcome = await Transport(Options(), http).SendAsync(payload, 1, new long[] { 10001 }, default);
        check(http.Count == 3 && outcome.TextOnlyRetry && !http.Payloads.Last().ToJsonString().Contains("image_url"), "empty choices fallback is bounded then strips images");
        check(payload.ToJsonString().Contains("image_url") && http.AllDisposed, "image fallback preserves caller payload and releases resources");
        var reason = new ProbeFetcher { Handler = (count, _) => Task.FromResult(Response(count == 1 ? 422 : 200, count == 1 ? "unsupported reasoning_effort " + Marker : Success)) };
        await Transport(Options(), reason).SendAsync(new JsonObject { ["reasoning_effort"] = "none" }, 0, Array.Empty<long>(), default);
        check(reason.Count == 2 && !reason.Payloads.Last().ContainsKey("reasoning_effort"), "existing reasoning unsupported fallback is one-shot");
    }

    private static async Task ImagesAsync(Action<bool, string> check)
    {
        var blocked = new ProbeFetcher();
        var images = new ModelImages(blocked) { AllowPrivateHosts = false };
        check(await images.DownloadBytesAsync("http://127.0.0.1/" + Marker, default) is null && blocked.Count == 0, "image SSRF refusal happens before IO");
        var redirect = new ProbeFetcher { Handler = (_, _) => { var r = Response(302, ""); r.Headers.Location = new Uri("http://127.0.0.1/" + Marker); return Task.FromResult(r); } };
        check(await new ModelImages(redirect) { AllowPrivateHosts = false }.DownloadBytesAsync("https://example.com/image", default) is null && redirect.Count == 1 && redirect.AllDisposed, "private redirect blocked and intermediate response disposed");
        var large = new ProbeFetcher { Handler = (_, _) => { var r = Response(200, "x"); r.Content.Headers.ContentLength = 6 * 1024 * 1024 + 1; return Task.FromResult(r); } };
        check(await new ModelImages(large).DownloadBytesAsync("https://example.com/large", default) is null && large.AllDisposed, "image declared size cap preserved");
        var streamed = new ProbeFetcher { Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new TrackedContent(new string('x', 6 * 1024 * 1024 + 1), unknownLength: true) }) };
        check(await new ModelImages(streamed).DownloadBytesAsync("https://example.com/chunked", default) is null && streamed.AllDisposed, "image actual stream cap preserved without Content-Length");
        var expired = new ProbeFetcher { Handler = (_, _) => Task.FromResult(Response(400, Marker)) };
        images = new ModelImages(expired) { RefreshUrls = (_, _) => throw new OperationCanceledException() };
        try { await images.DownloadBytesAsync("https://example.com/expired", default, 10001); check(false, "refresh cancellation expected"); }
        catch (OperationCanceledException) { check(expired.Count == 1 && expired.AllDisposed, "refresh callback cancellation is never swallowed"); }
    }

    private static async Task ConnectionAsync(Action<bool, string> check)
    {
        var route = new ModelProviderRoute("synthetic-provider", "https://example.com/v1", "synthetic-model", Marker);
        var http = new ProbeFetcher();
        var tested = await new ConnectionTester(http).TestAsync(route);
        var wire = http.Payloads.Single();
        check(tested.Succeeded && tested.ErrorCode == "ok" && http.Count == 1 && http.AllDisposed, "provider connection makes exactly one disposed synthetic request");
        check(wire["max_tokens"]!.GetValue<int>() == 64 && !wire.ContainsKey("temperature") && !wire.ContainsKey("top_p") && !wire.ContainsKey("reasoning_effort") && !wire.ContainsKey("stream"), "provider test has fixed minimal nonstreaming payload");
        foreach (var item in new[] { (401, "model_auth_failed"), (429, "model_rate_limited"), (503, "model_upstream_error"), (200, "model_malformed_response") })
        {
            http = new ProbeFetcher { Handler = (_, _) => Task.FromResult(Response(item.Item1, Marker)) };
            tested = await new ConnectionTester(http).TestAsync(route);
            check(!tested.Succeeded && tested.ErrorCode == item.Item2 && http.Count == 1 && http.AllDisposed, "provider failure distinct and never retried");
        }
        http = new ProbeFetcher { Handler = (_, _) => Task.FromResult(Response(200, new string('x', 65537))) };
        check((await new ConnectionTester(http).TestAsync(route)).ErrorCode == "model_response_too_large" && http.AllDisposed, "provider response hard cap is 64 KiB");
        http = new ProbeFetcher { Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new TrackedContent(new string('x', 65537), unknownLength: true) }) };
        check((await new ConnectionTester(http).TestAsync(route)).ErrorCode == "model_response_too_large" && http.AllDisposed, "provider actual stream cap does not require Content-Length");
        http = new ProbeFetcher { Handler = (_, _) => throw new TaskCanceledException(Marker) };
        check((await new ConnectionTester(http).TestAsync(route)).ErrorCode == "model_timeout" && http.Count == 1, "provider local timeout safe and not retried");
        using var cancel = new CancellationTokenSource();
        http = new ProbeFetcher { Handler = (_, _) => { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); } };
        try { await new ConnectionTester(http).TestAsync(route, cancel.Token); check(false, "provider cancellation expected"); }
        catch (OperationCanceledException) { check(http.Count == 1, "provider caller cancellation propagates to management journal"); }
    }

    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new TrackedContent(body) };

    private sealed class ProbeFetcher : BotAgent.Adapters.Net.IHttpFetcher, Fetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        internal int Count { get; private set; }
        internal List<JsonObject> Payloads { get; } = new();
        internal List<TrackedContent> Responses { get; } = new();
        internal List<HttpContent> Requests { get; } = new();
        internal Func<int, CancellationToken, Task<HttpResponseMessage>>? Handler { get; init; }
        internal bool AllDisposed => Responses.All(content => content.Disposed) && Requests.All(content => { try { _ = content.ReadAsStream(); return false; } catch (ObjectDisposedException) { return true; } });
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Count++;
            if (request.Content is { } content) { Requests.Add(content); Payloads.Add(JsonNode.Parse(await content.ReadAsStringAsync(ct))!.AsObject()); }
            var response = Handler is null ? Response(200, Success) : await Handler(Count, ct);
            if (response.Content is TrackedContent tracked) Responses.Add(tracked);
            return response;
        }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
        public async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) { using var r = new HttpRequestMessage(HttpMethod.Get, url); return await SendAsync(r, ct); }
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
        public async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) { using var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = content }; return await SendAsync(r, ct); }
    }

    private sealed class TrackedContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly bool _unknownLength;
        internal bool Disposed { get; private set; }
        internal TrackedContent(string text, bool unknownLength = false) { _bytes = System.Text.Encoding.UTF8.GetBytes(text); _unknownLength = unknownLength; }
        protected override bool TryComputeLength(out long length) { length = _bytes.Length; return !_unknownLength; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class InstantClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
        public DateTimeOffset UtcNow => Now;
        public DateTime LocalDateTime => Now.LocalDateTime;
        public long TickCount => 0;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Delay(TimeSpan.FromMilliseconds(millisecondsDelay), ct);
    }
}
