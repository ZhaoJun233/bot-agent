using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Platforms;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Platforms;
using BotAgent.Platforms.Net;
using BotAgent.Services.Official;

internal static class ApiResultChecks
{
    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        var cases = new (int Status, int Code, DeliveryStatus Expected, string Reason, bool Group)[]
        {
            (403, 11253, DeliveryStatus.Rejected, "permission_denied", true),
            (200, 11253, DeliveryStatus.Rejected, "permission_denied", false),
            (403, 11244, DeliveryStatus.Rejected, "permission_denied", true),
            (400, 22006, DeliveryStatus.Rejected, "invalid_parameters", true),
            (400, 304103, DeliveryStatus.Rejected, "reply_window_expired", true),
            (400, 40034005, DeliveryStatus.Rejected, "reply_window_expired", false),
            (400, 40034006, DeliveryStatus.Rejected, "content_rejected", true),
            (400, 40034100, DeliveryStatus.Throttled, "rate_limited", true),
            (400, 40034101, DeliveryStatus.Rejected, "relationship_removed", true),
            (400, 40034128, DeliveryStatus.Rejected, "reply_limit_reached", false),
            (400, 40054004, DeliveryStatus.Rejected, "relationship_removed", false),
            (400, 40054013, DeliveryStatus.Rejected, "recipient_refused", false),
            (400, 40054005, DeliveryStatus.Unknown, "duplicate_delivery", false),
            (500, 50055002, DeliveryStatus.Unknown, "delivery_outcome_unknown", false),
        };
        foreach (var item in cases)
            await test($"api: status {item.Status} code {item.Code} is truthful without retry", async () =>
            {
                using var f = new Fixture(item.Group);
                f.Http.Answer = _ => Response(new JsonObject { ["code"] = item.Code,
                    ["message"] = "synthetic-sensitive-body https://example.com/?token=test-token-synthetic-secret" }.ToJsonString(), item.Status);
                var result = await f.Send();
                Require(result.Status == item.Expected && result.ReasonCode == item.Reason, "wrong delivery classification");
                Require(f.Http.Attempts == 1 && f.Http.Tokens == 1, "rejected or unknown delivery retried");
                Require(f.Http.Bodies.Single()["msg_id"]?.GetValue<string>() == "synthetic-inbound", "passive trigger changed");
                Require(f.Logs.All(line => !line.Contains("synthetic-sensitive-body", StringComparison.Ordinal)
                    && !line.Contains("example.com", StringComparison.Ordinal)), "response body entered diagnostics");
            });

        foreach (var body in new[] { "{", "[]", "{}", "{\"code\":\"11244\"}",
            "{\"code\":0,\"code\":11244}", "{\"id\":false}", "{\"id\":\" \"}" })
            await test("api: malformed or ambiguous success is unknown", async () =>
            {
                using var f = new Fixture();
                f.Http.Answer = _ => Response(body);
                var result = await f.Send();
                Require(result.Status == DeliveryStatus.Unknown && result.ReasonCode == "invalid_response", "ambiguous response confirmed or threw");
                Require(f.Http.Attempts == 1 && f.Http.Tokens == 1, "ambiguous response replayed");
            });

        foreach (var status in new[] { 400, 403 })
            await test($"api: HTTP {status} contradicting a success receipt is unknown", async () =>
            {
                using var f = new Fixture();
                f.Http.Answer = _ => Response("""{"code":0,"id":"synthetic-outbound"}""", status);
                var result = await f.Send();
                Require(result.Status == DeliveryStatus.Unknown && result.MessageId is null, "contradictory receipt treated as a definite rejection");
                Require(f.Http.Attempts == 1 && f.Http.Tokens == 1, "contradictory receipt replayed");
            });

        await test("api: body substrings and nested codes cannot refresh credentials", async () =>
        {
            using var f = new Fixture();
            f.Http.Answer = _ => Response("""{"code":22006,"message":"11244 11253 synthetic-sensitive-body","extra":{"code":11244}}""", 400);
            var result = await f.Send();
            Require(result.ReasonCode == "invalid_parameters" && f.Http.Attempts == 1 && f.Http.Tokens == 1, "body text selected credential retry");
            Require(f.Logs.All(line => !line.Contains("synthetic-sensitive-body", StringComparison.Ordinal)), "error content logged");
        });
        await test("api: HTTP throttling keeps Retry-After without replay", async () =>
        {
            using var f = new Fixture();
            f.Http.Answer = _ =>
            {
                var response = Response("synthetic-sensitive-body", 429);
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(7));
                return response;
            };
            var result = await f.Send();
            Require(result.Status == DeliveryStatus.Throttled && result.RetryAfterSeconds == 7, "retry delay lost");
            Require(f.Http.Attempts == 1, "rate limit retried");
        });
        foreach (var failure in new[] { "network", "timeout", "cancellation" })
            await test("api: " + failure + " after invocation is unknown", async () =>
            {
                using var f = new Fixture();
                f.Http.Answer = _ => throw failure switch
                {
                    "network" => new HttpRequestException("synthetic-sensitive-body"),
                    "timeout" => new TaskCanceledException("synthetic-sensitive-body"),
                    _ => new OperationCanceledException("synthetic-sensitive-body", new CancellationToken(true)),
                };
                var result = await f.Send();
                Require(result.Status == DeliveryStatus.Unknown && result.ReasonCode == "delivery_outcome_unknown", "uncertain send treated as retryable");
                Require(f.Http.Attempts == 1, "uncertain send repeated");
            });
        await test("api: successful acknowledgement retains a scoped message alias", async () =>
        {
            using var f = new Fixture();
            var result = await f.Send();
            Require(result.Status == DeliveryStatus.Success && long.TryParse(result.MessageId, out var id) && id >= Channels.AliasBase, "valid acknowledgement lost");
            Require(f.Http.Attempts == 1 && f.Http.Tokens == 1, "success repeated");
        });
        await test("api: explicit success without id has no fabricated reference", async () =>
        {
            using var f = new Fixture();
            f.Http.Answer = _ => Response("""{"code":0}""");
            var result = await f.Send();
            Require(result.Status == DeliveryStatus.Success && result.MessageId is null && result.ReasonCode == "message_id_unavailable", "missing message id hidden or invented");
            Require(f.Http.Attempts == 1, "id-less success repeated");
        });
        foreach (var status in new[] { 400, 401 })
            await test("api: exact credential rejection retries once with the same trigger", async () =>
            {
                using var f = new Fixture();
                f.Http.Answer = attempt => attempt == 1 ? Response("""{"code":11244}""", status) : Response("""{"id":"synthetic-ack"}""");
                var result = await f.Send();
                Require(result.IsSuccess && f.Http.Attempts == 2 && f.Http.Tokens == 2, "exact credential retry missing");
                Require(f.Http.Bodies[0].ToJsonString() == f.Http.Bodies[1].ToJsonString(), "token retry changed sequence or trigger");
            });
        await test("api: response size is bounded", async () =>
        {
            using var f = new Fixture();
            f.Http.Answer = _ => Response(new JsonObject { ["id"] = new string('x', 70_000) }.ToJsonString());
            var result = await f.Send();
            Require(result.Status == DeliveryStatus.Unknown && result.ReasonCode == "response_too_large", "oversize response trusted");
            Require(f.Http.Attempts == 1, "oversize response replayed");
        });
        foreach (var tokenBody in new[] { "{\"code\":400,\"message\":\"synthetic-sensitive-body\"}",
            "{\"code\":400,\"access_token\":\"synthetic-token\"}", "{\"access_token\":true}" })
            await test("api: invalid credentials response never reaches message transport", async () =>
            {
                using var f = new Fixture();
                f.Http.TokenBody = tokenBody;
                var result = await f.Send();
                Require(!result.IsSuccess && result.ReasonCode == "authentication_unavailable", "invalid credential response trusted or exposed");
                Require(f.Http.Attempts == 0 && f.Http.Tokens == 1, "message attempted without valid credential");
            });
        await test("api: documented decimal string token lifetime is accepted", async () =>
        {
            using var f = new Fixture();
            f.Http.TokenBody = """{"access_token":"synthetic-token","expires_in":"7200"}""";
            Require((await f.Send()).IsSuccess, "documented string token lifetime rejected");
            Require(f.Http.Attempts == 1 && f.Http.Tokens == 1, "valid token response repeated");
        });
        await test("api: token lifetime rejects malformed strings and nonpositive or nonintegral values", async () =>
        {
            JsonNode?[] invalid = ["", "0", "-1", "+7200", " 7200", "7200 ", "7.2e3",
                "7,200", "７２００", "2147483648", "7200\0", true, 0, -1, 1.5, null];
            foreach (var lifetime in invalid)
            {
                using var f = new Fixture();
                f.Http.TokenBody = new JsonObject { ["access_token"] = "synthetic-token",
                    ["expires_in"] = lifetime }.ToJsonString();
                var result = await f.Send();
                Require(result.ReasonCode == "authentication_unavailable", "invalid lifetime trusted");
                Require(f.Http.Attempts == 0 && f.Http.Tokens == 1, "invalid lifetime reached message transport");
            }
        });
        await test("api: diagnostics contain no token fragments or rejected event data", async () =>
        {
            using var f = new Fixture();
            f.Http.TokenBody = """{"access_token":"sensitive-access-token-canary","expires_in":7200}""";
            Require((await f.Send()).IsSuccess, "token log check did not send");
            f.RejectedEvent();
            Require(f.Logs.All(line => !line.Contains("sens***ry", StringComparison.Ordinal)
                && !line.Contains("synthetic-sensitive-body", StringComparison.Ordinal)), "sensitive diagnostic content retained");
        });
        await test("api: conflicting receipt and error never authorizes token retry", async () =>
        {
            using var f = new Fixture();
            f.Http.Answer = _ => Response("""{"code":11244,"id":"synthetic-ack"}""", 400);
            var result = await f.Send();
            Require(result.Status == DeliveryStatus.Unknown && result.ReasonCode == "invalid_response", "conflicting receipt replayed");
            Require(f.Http.Attempts == 1 && f.Http.Tokens == 1, "conflicting receipt triggered refresh");
        });
        await test("api: malformed optional upload lifetime is not parsed as a number", async () =>
        {
            using var f = new Fixture();
            f.Http.Answer = attempt => attempt == 1 ? Response("""{"file_info":"synthetic-file","ttl":"invalid"}""")
                : Response("""{"id":"synthetic-media-ack"}""");
            Require(await f.SendImage(), "valid upload without usable lifetime failed");
            Require(f.Http.Attempts == 2 && f.Logs.Any(line => line.Contains("ttl=unknown", StringComparison.Ordinal)), "unusable upload lifetime trusted");
        });
    }

    private static HttpResponseMessage Response(string body, int status = 200)
        => new((HttpStatusCode)status) { Content = new StringContent(body) };

    private sealed class Fixture : IDisposable
    {
        private readonly SyntheticDirectory _directory = new();
        private readonly OfficialBotGateway _gateway;
        private readonly QqChatSourcePlatformAdapter _adapter;
        private readonly ConversationId _target;
        private readonly BotAgent.Services.Qq.PlatformSourceLease _source;
        public ApiHttp Http { get; } = new();
        public List<string> Logs { get; } = new();
        public Fixture(bool group = true)
        {
            var ids = new OfficialIdentityMap(_directory.Map);
            var raw = group ? "synthetic-group" : "synthetic-member";
            var alias = ids.AliasFor(new(new("app-A", group ? OfficialIdentityKind.Group : OfficialIdentityKind.C2cUser), raw));
            _gateway = new(new PlatformSettingsBox(new PlatformOptions { OfficialEnabled = true,
                OfficialAppId = "app-A", OfficialAppSecret = "synthetic-secret",
                OfficialApiBase = "https://example.com", OfficialTokenUrl = "https://example.com/token" }), Http, () => ids, Logs.Add,
                () => OfficialProbeContext.OpenLedger(_directory.Map));
            _adapter = new(_gateway, new(PlatformId.QqOfficial, AccountScope.Legacy), PlatformCapabilities.QqOfficial, "synthetic", "synthetic");
            _target = new(PlatformId.QqOfficial, AccountScope.Legacy, group ? ConversationKind.GroupChat : ConversationKind.PrivateChat,
                alias.ToString(CultureInfo.InvariantCulture));
            var inbound = new JsonObject { ["id"] = "synthetic-inbound", ["content"] = "synthetic-message",
                ["timestamp"] = PlatformClock.Current.Now.ToString("O"),
                ["author"] = new JsonObject { [group ? "member_openid" : "user_openid"] = "synthetic-member" } };
            if (group) inbound["group_openid"] = raw;
            typeof(OfficialBotGateway).GetMethod("HandleDispatch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_gateway, [group ? "GROUP_AT_MESSAGE_CREATE" : "C2C_MESSAGE_CREATE", inbound, null]);
            var trigger = ids.AliasFor(new(new("app-A", OfficialIdentityKind.Message,
                new(PlatformId.QqOfficial, "app-A", _target.Kind, raw), OfficialMessageDirection.Inbound), "synthetic-inbound"));
            _source = OfficialProbeContext.Source(_gateway, group, alias, trigger);
        }
        public async Task<DeliveryResult> Send()
        { using var source = BotAgent.Services.Qq.PlatformSourceExecution.Enter(_source); return await _adapter.SendAsync(_adapter.Context, new(_target, "synthetic-output")); }
        public async Task<bool> SendImage()
        { using var source = BotAgent.Services.Qq.PlatformSourceExecution.Enter(_source); return await _gateway.SendImageAsync(true, long.Parse(_target.NativeTargetId, CultureInfo.InvariantCulture), [1, 2, 3]); }
        public void RejectedEvent()
            => typeof(OfficialBotGateway).GetMethod("HandleDispatch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_gateway, ["REJECT_synthetic-sensitive-body", new JsonObject(), new JsonObject { ["message"] = "synthetic-sensitive-body" }]);
        public void Dispose() { _adapter.Dispose(); _gateway.Dispose(); _directory.Dispose(); }
    }

    private sealed class ApiHttp : IPlatformHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public int Attempts { get; private set; }
        public int Tokens { get; private set; }
        public List<JsonObject> Bodies { get; } = new();
        public Func<int, HttpResponseMessage> Answer { get; set; } = _ => Response("""{"id":"synthetic-ack"}""");
        public string TokenBody { get; set; } = """{"access_token":"synthetic-token","expires_in":7200}""";
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            Attempts++;
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            return Answer(Attempts);
        }
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        { Tokens++; return Task.FromResult(Response(TokenBody)); }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new InvalidOperationException("unexpected_request");
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
    }
}
