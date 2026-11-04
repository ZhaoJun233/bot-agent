using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Model;
using BotAgent.Model;
using BotAgent.Model.Protocols;
using BotAgent.Services.Model;

/// <summary>
/// Opt-in, bounded synthetic cache experiment. No configuration files, production conversation,
/// transport retry/failover, response bodies, credentials or endpoint names enter diagnostic output.
/// Environment contract: BOTAGENT_LIVE_CACHE=1 plus --live-cache; BASE_URL/API_KEY/MODEL/PROTOCOL
/// use the BOTAGENT_LIVE_CACHE_ prefix. Optional TIMEOUT_SECONDS (1..120, default 60),
/// ALLOW_INSECURE=1 for HTTP or loopback. All seven requests are prepared and bounded before sending.
/// </summary>
internal static class LiveCacheChecks
{
    private const string EnvPrefix = "BOTAGENT_LIVE_CACHE_";
    private const int MaxRequests = 8;
    private const int PlannedRequests = 7; // The eighth authorization slot remains unused.
    private const int MaxInputTokens = 16000;
    private const int MaxOutputTokens = 64;
    private const int FramingAllowance = 512;
    private const int MaxResponseBytes = 128 * 1024;

    internal static async Task<int> RunAsync()
    {
        var sent = 0;
        try
        {
            var config = ReadConfiguration();
            BotAgent.Model.ModelClock.Initialize(PromptCacheChecks.ProbeClock);
            var plan = await PrepareAsync(config.Route);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var observations = new List<Observation>();
            foreach (var call in plan)
            {
                if (sent >= MaxRequests || sent >= PlannedRequests) throw new ProbeFailure("request_budget");
                using var request = ModelProtocolAdapter.CreateRequest(config.Route, call.Built.Payload,
                    stableSystemPrefixLength: call.Built.StableSystemPrefixLength);
                CheckOrigin(request.RequestUri!, config.Route.BaseUrl);
                request.Version = HttpVersion.Version11;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                request.Headers.ConnectionClose = true; // No pooled stale-connection replay or HTTP/2 fallback.
                var wire = await request.Content!.ReadAsStringAsync();
                Require(Hash(wire) == call.WireHash, "wire_changed_after_preflight");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
                sent++;
                var watch = Stopwatch.StartNew();
                // Deliberately no ModelTransport.SendAsync: its production retries would exceed this experiment's authority.
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { status = "aborted", httpStatus = (int)response.StatusCode, requests = sent }));
                    return 2; // Includes auth, throttling, unsupported parameters and all redirect responses; never read their bodies.
                }
                var raw = await ReadBoundedAsync(response.Content, deadline.Token);
                watch.Stop();
                var native = ReadNative(raw, config.Route.Protocol);
                Require(native.Input is not null && native.Output is not null, "native_usage_missing");
                if (native.TotalInput(config.Route.Protocol) > MaxInputTokens || native.Output > MaxOutputTokens)
                {
                    Console.WriteLine(FormatBudgetFailure(sent, native));
                    return 2; // Preserve numeric evidence, then stop; never issue a replacement request.
                }
                var normalized = ModelProtocolAdapter.NormalizeResponse(config.Route.Protocol, raw);
                Require(ModelJson.HasChoices(normalized), "completion_shape_missing");
                var parsed = ModelJson.ReadUsageDetail(normalized);
                var consistent = native.Matches(parsed);
                observations.Add(new Observation(call.Phase, call.Index, native.Read, consistent));
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    phase = call.Phase, index = call.Index, requests = sent,
                    nativeUsage = new { input = native.Input, output = native.Output, cacheRead = native.Read, cacheWrite = native.Write },
                    parsedUsage = new { input = parsed.PromptTokens, output = parsed.CompletionTokens, cacheRead = parsed.CachedPromptTokens, cacheWrite = parsed.CacheWriteTokens },
                    consistent, stableHash = call.StableHash, stableUtf8Bytes = call.StableBytes,
                    dynamicHash = call.DynamicHash, dynamicUtf8Bytes = call.DynamicBytes,
                    wireHash = call.WireHash, wireUtf8Bytes = call.WireBytes,
                    inputTokenUpperBound = call.WireBytes + FramingAllowance, latencyMs = watch.ElapsedMilliseconds
                }));
                Require(consistent, "usage_mismatch");
            }
            var warm = observations.Where(item => item.Phase == "dynamic" && item.Index is 2 or 3).ToArray();
            // Missing native cache telemetry never becomes a zero or evidence of a cache miss.
            var proved = warm.Length == 2 && warm.All(item => item.Read > 0 && item.Consistent);
            Console.WriteLine(JsonSerializer.Serialize(new { status = proved ? "cache_proved" : "cache_not_proved", requests = sent }));
            return proved ? 0 : 1;
        }
        catch (ProbeFailure failure)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "aborted", code = failure.Code, requests = sent }));
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "aborted", code = "timeout_or_cancelled", requests = sent }));
            return 2;
        }
        catch (Exception)
        {
            // Never print exception messages: HTTP and adapter errors can contain URLs, payloads or credentials.
            Console.WriteLine(JsonSerializer.Serialize(new { status = "aborted", code = "local_or_transport_error", requests = sent }));
            return 2;
        }
    }

    internal static async Task RunOfflineAsync(Action<bool, string> check)
    {
        foreach (var protocol in new[] { ModelProtocol.OpenAiChat, ModelProtocol.OpenAiResponses, ModelProtocol.AnthropicMessages })
        {
            var route = new ModelProviderRoute("synthetic-live-cache", "https://example.com/v1", "example-model", "synthetic-key",
                protocol, new ModelProtocolOptions(PromptCache: true));
            var plan = await PrepareAsync(route);
            check(plan.Count == PlannedRequests && plan.Count < MaxRequests, "live cache plan reserves one request slot");
            check(plan.All(item => item.WireBytes + FramingAllowance <= MaxInputTokens), "live cache input bound covers every protocol request");
            check(plan.Take(3).Select(item => item.WireHash).Distinct().Count() == 1, "live cache fixed baseline is byte-identical");
            check(plan.Take(6).Select(item => item.StableHash).Distinct().Count() == 1, "live cache real path retains stable prefix");
            check(plan.Skip(3).Take(3).Select(item => item.DynamicHash).Distinct().Count() == 3, "live cache real path changes dynamic context");
            check(plan[6].StableHash != plan[0].StableHash, "live cache negative control changes initial prefix");
        }
        var unknown = ReadNative("{\"usage\":{\"prompt_tokens\":8,\"completion_tokens\":1}}", ModelProtocol.OpenAiChat);
        check(unknown.Read is null && unknown.Write is null, "live cache unknown native telemetry remains unknown");
        var explicitZero = ReadNative("{\"usage\":{\"prompt_tokens\":8,\"completion_tokens\":1,\"prompt_tokens_details\":{\"cached_tokens\":0}}}", ModelProtocol.OpenAiChat);
        check(explicitZero.Read == 0, "live cache explicit native zero is preserved");
        using var failure = JsonDocument.Parse(FormatBudgetFailure(4, new NativeUsage(MaxInputTokens + 1, MaxOutputTokens + 1, null, null)));
        var counters = failure.RootElement.GetProperty("nativeUsage");
        check(failure.RootElement.GetProperty("code").GetString() == "reported_token_budget"
            && counters.GetProperty("input").GetInt32() == MaxInputTokens + 1
            && counters.GetProperty("output").GetInt32() == MaxOutputTokens + 1, "live cache budget failure preserves only observed counters");
        check(failure.RootElement.EnumerateObject().Count() == 4 && counters.EnumerateObject().Count() == 4
            && counters.GetProperty("cacheRead").ValueKind == JsonValueKind.Null
            && counters.GetProperty("cacheWrite").ValueKind == JsonValueKind.Null, "live cache budget failure excludes payload and preserves unknown usage");
    }

    private static string FormatBudgetFailure(int sent, NativeUsage native) => JsonSerializer.Serialize(new
    {
        status = "aborted", code = "reported_token_budget", requests = sent,
        nativeUsage = new { input = native.Input, output = native.Output, cacheRead = native.Read, cacheWrite = native.Write }
    });

    private static Configuration ReadConfiguration()
    {
        Require(Environment.GetEnvironmentVariable("BOTAGENT_LIVE_CACHE") == "1", "explicit_enable_required");
        var url = Required("BASE_URL");
        Require(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment), "endpoint_invalid");
        Require(uri!.Scheme == "https" && !uri.IsLoopback || Environment.GetEnvironmentVariable(EnvPrefix + "ALLOW_INSECURE") == "1",
            "insecure_endpoint_requires_opt_in");
        var protocol = Required("PROTOCOL") switch
        {
            "OpenAiChat" => ModelProtocol.OpenAiChat,
            "OpenAiResponses" => ModelProtocol.OpenAiResponses,
            "AnthropicMessages" => ModelProtocol.AnthropicMessages,
            _ => throw new ProbeFailure("protocol_unsupported")
        };
        var timeoutText = Environment.GetEnvironmentVariable(EnvPrefix + "TIMEOUT_SECONDS");
        var timeout = 60;
        if (timeoutText is not null)
            Require(int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out timeout) && timeout is >= 1 and <= 120,
                "timeout_invalid");
        var route = new ModelProviderRoute("synthetic-live-cache", uri.AbsoluteUri.TrimEnd('/'), Required("MODEL"), Required("API_KEY"),
            protocol, new ModelProtocolOptions(TimeoutSeconds: timeout, PromptCache: true));
        return new Configuration(route, timeout);
    }

    private static async Task<List<PreparedCall>> PrepareAsync(ModelProviderRoute route)
    {
        using var clockScope = new SyntheticClockScope();
        // The fake fetcher is an assertion: BuildAsync must never fetch media or send a model request.
        var noNetwork = new FakeHttp((_, _) => throw new ProbeFailure("unexpected_build_network"));
        var options = new ModelOptions { Model = route.ModelName, MaxTokens = MaxOutputTokens, ThinkingBudget = "off" };
        var transport = new ModelTransport(() => options, noNetwork, noNetwork, new ImageDownloader(noNetwork), TimeSpan.FromSeconds(60));
        var window = new[] { new ChatMessage { Role = MessageRole.Peer, SenderId = 10001, Timestamp = SyntheticTime,
            Text = "Audit all synthetic incident tickets against the runbooks. Select the lowest-numbered ticket requiring immediate escalation. Reply with its ticket ID and one short reason, under 12 words, in the normal JSON reply field." } };
        var baseRequest = new PromptBuilder.PromptRequest(SyntheticPolicy, null, null, 0, window, [], null, null, false, false,
            null, null, null, null, null, null, null, 0, false, false, 80, 0, false, false, false, IsPrivateConversation: true);
        var plan = new List<PreparedCall>();
        var fixedParts = PromptBuilder.BuildParts(baseRequest with { ProfilesText = "Synthetic operator A: triage shift alpha." });
        for (var index = 1; index <= 3; index++) await AddAsync("fixed", index, fixedParts);
        for (var index = 1; index <= 3; index++)
        {
            var now = SyntheticTime.AddMinutes(index);
            PromptCacheChecks.ProbeClock.CurrentTime = now;
            var currentWindow = new[] { new ChatMessage { Role = MessageRole.Peer, SenderId = 10001, Timestamp = now,
                Text = window[0].Text + $" This is synthetic handoff {index}; recheck the same escalation criteria." } };
            var parts = PromptBuilder.BuildParts(baseRequest with
            {
                ProfilesText = $"Synthetic operator A: triage shift {index}; handoff counter {index}.",
                MoodText = $"Synthetic session checkpoint {index}; remain concise.",
                EpisodesText = $"Synthetic memory: handoff {index} has not yet been reviewed.",
                Window = currentWindow
            });
            Require(parts.Stable == fixedParts.Stable, "stable_prefix_changed");
            await AddAsync("dynamic", index, parts, currentWindow);
        }
        var negative = PromptBuilder.BuildParts(baseRequest with
        {
            SystemPrompt = "Control task: use the following synthetic runbooks without assuming earlier context.\n" + SyntheticPolicy,
            ProfilesText = "Synthetic operator A: isolated control shift."
        });
        await AddAsync("negative", 1, negative);
        Require(plan.Count == PlannedRequests && plan.Take(3).Select(item => item.WireHash).Distinct().Count() == 1
            && plan.Skip(3).Take(3).Select(item => item.DynamicHash).Distinct().Count() == 3
            && plan[6].StableHash != plan[0].StableHash, "plan_shape_invalid");
        return plan;

        async Task AddAsync(string phase, int index, PromptBuilder.PromptParts parts, IReadOnlyList<ChatMessage>? messages = null)
        {
            var built = (await transport.BuildAsync(messages ?? window, parts.Text, [], CancellationToken.None, promptCacheScope: "synthetic-live-cache"))
                with { StableSystemPrefixLength = parts.Stable.Length };
            using var request = ModelProtocolAdapter.CreateRequest(route, built.Payload, stableSystemPrefixLength: built.StableSystemPrefixLength);
            CheckOrigin(request.RequestUri!, route.BaseUrl);
            var wire = await request.Content!.ReadAsStringAsync();
            var bytes = Encoding.UTF8.GetByteCount(wire);
            Require(bytes + FramingAllowance <= MaxInputTokens, "input_token_upper_bound");
            using var doc = JsonDocument.Parse(wire);
            var capField = route.Protocol == ModelProtocol.OpenAiResponses ? "max_output_tokens" : "max_tokens";
            Require(GetTokens(doc.RootElement, capField) is > 0 and <= MaxOutputTokens, "output_token_cap_missing");
            Require(built.AttachedImages == 0 && parts.Stable.Length > 0, "synthetic_request_shape");
            plan.Add(new PreparedCall(phase, index, built, Hash(parts.Stable), Encoding.UTF8.GetByteCount(parts.Stable),
                Hash(parts.Dynamic), Encoding.UTF8.GetByteCount(parts.Dynamic), Hash(wire), bytes));
        }
    }

    private static NativeUsage ReadNative(string json, ModelProtocol protocol)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("usage", out var usage)) return new NativeUsage(null, null, null, null);
        return protocol switch
        {
            ModelProtocol.OpenAiChat => new(GetTokens(usage, "prompt_tokens"), GetTokens(usage, "completion_tokens"),
                GetTokens(usage, "prompt_tokens_details", "cached_tokens") ?? GetTokens(usage, "prompt_cache_hit_tokens"), GetTokens(usage, "cache_write_tokens")),
            ModelProtocol.OpenAiResponses => new(GetTokens(usage, "input_tokens"), GetTokens(usage, "output_tokens"),
                GetTokens(usage, "input_tokens_details", "cached_tokens"), null),
            ModelProtocol.AnthropicMessages => new(GetTokens(usage, "input_tokens"), GetTokens(usage, "output_tokens"),
                GetTokens(usage, "cache_read_input_tokens"), GetTokens(usage, "cache_creation_input_tokens")),
            _ => throw new ProbeFailure("protocol_unsupported")
        };
    }

    private static int? GetTokens(JsonElement value, params string[] path)
    {
        foreach (var key in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0 ? count : null;
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        Require(content.Headers.ContentLength is null or <= MaxResponseBytes, "response_too_large");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            Require(buffer.Length + count <= MaxResponseBytes, "response_too_large");
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void CheckOrigin(Uri target, string baseUrl)
    {
        var expected = new Uri(baseUrl);
        Require(target.Scheme == expected.Scheme && target.IdnHost == expected.IdnHost && target.Port == expected.Port, "origin_changed");
    }

    private static string Required(string suffix)
    {
        var value = Environment.GetEnvironmentVariable(EnvPrefix + suffix);
        Require(!string.IsNullOrWhiteSpace(value), "configuration_missing");
        return value!;
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static void Require(bool condition, string code) { if (!condition) throw new ProbeFailure(code); }
    private sealed class ProbeFailure(string code) : Exception { public string Code { get; } = code; }
    private sealed class Configuration(ModelProviderRoute route, int timeoutSeconds)
    {
        public ModelProviderRoute Route { get; } = route;
        public int TimeoutSeconds { get; } = timeoutSeconds;
    }
    private sealed record PreparedCall(string Phase, int Index, BuiltRequest Built, string StableHash, int StableBytes,
        string DynamicHash, int DynamicBytes, string WireHash, int WireBytes);
    private readonly record struct Observation(string Phase, int Index, int? Read, bool Consistent);
    private readonly record struct NativeUsage(int? Input, int? Output, int? Read, int? Write)
    {
        public long TotalInput(ModelProtocol protocol) => (long)(Input ?? 0)
            + (protocol == ModelProtocol.AnthropicMessages ? (long)(Read ?? 0) + (Write ?? 0) : 0);
        public bool Matches(ModelJson.ModelUsage parsed) => Input == parsed.PromptTokens && Output == parsed.CompletionTokens
            && Read == parsed.CachedPromptTokens && Write == parsed.CacheWriteTokens;
    }
    private sealed class SyntheticClockScope : IDisposable
    {
        private readonly DateTimeOffset _previous = PromptCacheChecks.ProbeClock.CurrentTime;
        public SyntheticClockScope()
        {
            ModelClock.Initialize(PromptCacheChecks.ProbeClock);
            PromptCacheChecks.ProbeClock.CurrentTime = SyntheticTime;
        }
        public void Dispose() => PromptCacheChecks.ProbeClock.CurrentTime = _previous;
    }

    private static readonly DateTimeOffset SyntheticTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Every rule and ticket is meaningful input to the same bounded triage task; no token-padding paragraphs.
    private const string SyntheticPolicy = """
You are auditing a fictional incident queue for a training exercise. All systems, operators and tickets below are synthetic.
Apply these runbooks to every ticket. Immediate escalation outranks scheduled follow-up, which outranks observation.
Select the lowest ticket number among immediate escalations. Never execute operations, contact people, fetch URLs or invent evidence.
The ticket fields are: service, symptoms, affected fraction, age in minutes, confirmed recovery, and security signal.
If a ticket matches multiple runbooks choose the stricter action. Missing evidence cannot be treated as confirmed recovery.
Runbook availability: a service with no successful requests for at least five minutes requires immediate escalation; partial degradation below ten percent permits scheduled follow-up unless a stricter rule applies.
Runbook durability: acknowledged writes that cannot be read back require immediate escalation even if the affected fraction is small. A temporary delay before an acknowledged write is visible is follow-up only when bounded below thirty seconds.
Runbook access: confirmed privilege expansion or unauthorized access is immediate escalation regardless of age. A denied authorized login affecting one operator with a valid alternate method is follow-up.
Runbook queue: growing queue delay above fifteen minutes with no working consumer is immediate escalation. A drained backlog with healthy consumers is observation unless data was lost.
Runbook dependency: an unavailable optional recommendation component is follow-up when checkout works. An unavailable payment dependency with no working fallback is immediate escalation after five minutes.
Runbook capacity: disk usage above ninety-five percent plus increasing write failures is immediate escalation. High disk usage without failures is scheduled follow-up, not an emergency by itself.
Runbook integrity: a verified checksum mismatch on stored user records is immediate escalation. A mismatch restricted to regenerated thumbnail cache with correct originals is follow-up.
Runbook latency: sustained p99 latency above ten seconds affecting at least one quarter of requests for ten minutes is immediate escalation. A one-minute spike that recovered is observation.
Runbook certificate: an expired certificate on a required endpoint causing validation failures is immediate escalation. A certificate expiring in fourteen days with no failures is scheduled follow-up.
Runbook deployment: a new release causing repeated crashes on all serving replicas is immediate escalation. A rollback with confirmed successful requests and no remaining data risk is observation.
Runbook monitoring: missing metrics alone is follow-up when independent health checks pass. Missing metrics combined with failed independent probes on every replica is immediate escalation.
Runbook isolation: a tenant seeing another tenant's records is immediate escalation. An intentionally denied cross-tenant request without disclosed data is observation.
Runbook backup: one missed backup with a recent verified restore point is follow-up. Loss of the only recoverable copy of acknowledged data is immediate escalation.
Runbook limits: rate limiting an abusive synthetic client with healthy normal traffic is observation. A quota error blocking all legitimate operations without fallback is immediate escalation.
Runbook recovery: a claimed repair without a successful post-repair probe is unconfirmed and does not downgrade an immediate escalation. A validated repair can downgrade availability-only faults to observation.
Runbook ordering: do not mistake the largest ticket number for priority. Compare actions first, then choose the smallest numeric ticket identifier among immediate escalations.
T01: recommendation; optional suggestions unavailable, checkout healthy; affected 8 percent; age 20; recovery no; security none.
T02: metrics; telemetry missing, independent probes healthy; affected 100 percent; age 12; recovery no; security none.
T03: image-cache; regenerated thumbnail checksum mismatch, original records correct; affected 6 percent; age 9; recovery no; security none.
T04: login; one authorized operator denied, alternate method valid; affected 1 percent; age 30; recovery no; security none.
T05: storage; disk utilization 93 percent with no write errors; affected 0 percent; age 40; recovery no; security none.
T06: queue; backlog drained, all consumers healthy, no data lost; affected 0 percent; age 18; recovery yes; security none.
T07: api; p99 latency twelve seconds for one minute, normal afterward; affected 30 percent; age 1; recovery yes; security none.
T08: payment; required payment dependency unavailable, no fallback, zero successful payments; affected 100 percent; age 8; recovery no; security none.
T09: database; acknowledged writes fail read-back, no recent deletion; affected 2 percent; age 3; recovery no; security none.
T10: admin; verified unexpected privilege expansion; affected 1 percent; age 1; recovery no; security confirmed privilege expansion.
T11: queue; delay twenty-one minutes and no working consumer; affected 60 percent; age 21; recovery no; security none.
T12: storage; disk utilization 98 percent and increasing write failures; affected 12 percent; age 7; recovery no; security none.
T13: records; verified checksum mismatch on stored original records; affected 1 percent; age 2; recovery no; security none.
T14: api; p99 latency eleven seconds sustained for fourteen minutes; affected 40 percent; age 14; recovery no; security none.
T15: checkout; required endpoint certificate expired, validation fails; affected 100 percent; age 4; recovery no; security none.
T16: frontend; all serving replicas crash after deployment; affected 100 percent; age 6; recovery no; security none.
T17: monitoring; missing metrics and failed independent probes on every replica; affected 100 percent; age 10; recovery no; security none.
T18: tenant-store; synthetic tenant observed another tenant's records; affected 1 percent; age 1; recovery no; security confirmed disclosure.
T19: backup; one missed backup but recent verified restore point available; affected 0 percent; age 60; recovery no; security none.
T20: rate-limit; abusive synthetic client throttled, normal traffic healthy; affected 0 percent; age 15; recovery no; security none.
T21: api; all legitimate operations blocked by quota, no fallback; affected 100 percent; age 3; recovery no; security none.
T22: archive; only recoverable copy of acknowledged data lost; affected 2 percent; age 5; recovery no; security none.
T23: api; outage repair claimed but no post-repair probe succeeded; affected 100 percent; age 16; recovery unconfirmed; security none.
T24: frontend; rollback validated by successful requests, no residual data risk; affected 0 percent; age 11; recovery yes; security none.
""";
}
