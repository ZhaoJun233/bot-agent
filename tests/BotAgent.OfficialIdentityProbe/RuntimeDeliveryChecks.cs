using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Official;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Platforms;
using BotAgent.Platforms.Net;
using BotAgent.Services;
using BotAgent.Services.Official;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

internal static partial class RuntimeDeliveryChecks
{
    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        await test("runtime delivery keeps the original trigger when newer messages arrive", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); var source = f.Lease("first");
            using var execution = PlatformSourceExecution.Enter(source);
            f.Receive("second");
            Require((await f.Send()).Ok && f.Http.Messages.Single()["msg_id"]!.GetValue<string>() == "first", "latest event stole direct command trigger");
            using var binding = ReplyDeliveryExecution.Enter(source, f.Ref("second"));
            f.Receive("third");
            Require((await f.Send()).Ok && f.Http.Messages.Last()["msg_id"]!.GetValue<string>() == "second", "generation selection was replaced");
        });
        await test("runtime delivery refuses absent and explicit empty provenance before HTTP", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first");
            Require(!(await f.Send()).Ok, "triggerless send was active");
            var source = f.Lease("first"); using var execution = PlatformSourceExecution.Enter(source);
            using var binding = ReplyDeliveryExecution.Enter(source, null);
            Require(!(await f.Send()).Ok && f.Http.Tokens == 0 && f.Http.Messages.Count == 0, "empty binding borrowed ingress trigger");
        });
        await test("runtime delivery checks exact source instance and scoped inbound identity", async () =>
        {
            using var f = new Fixture(clock); using var other = new Fixture(clock); f.Receive("first"); other.Receive("first");
            using (PlatformSourceExecution.Enter(other.Lease("first"))) Require(!(await f.Send()).Ok, "foreign driver sent");
            using (PlatformSourceExecution.Enter(f.Lease("first")))
            {
                var outgoing = f.Ids.AliasFor(f.MessageKey("outgoing", OfficialMessageDirection.Outbound));
                using var binding = ReplyDeliveryExecution.Enter(PlatformSourceExecution.Current, new(f.Target, outgoing.ToString()));
                Require(!(await f.Send()).Ok, "outbound receipt became trigger");
            }
            f.Current = false;
            using (PlatformSourceExecution.Enter(f.Lease("first"))) Require(!(await f.Send()).Ok, "retired source sent");
            Require(f.Http.Tokens == 0 && f.Http.Messages.Count == 0, "invalid source reached network");
        });
        await test("runtime delivery enforces group and C2C budgets using event time", async () =>
        {
            foreach (var group in new[] { true, false })
            {
                using var f = new Fixture(clock, group); f.Receive("first");
                using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
                if (!group) clock.Now += TimeSpan.FromMinutes(59);
                var budget = group ? 5 : 4;
                for (var i = 1; i <= budget; i++) Require((await f.Send()).Ok, "valid budget was refused");
                Require(!(await f.Send()).Ok && f.Http.Messages.Count == budget, "budget overflow reached HTTP");
                Require(f.Http.Messages.Select(m => m["msg_seq"]!.GetValue<int>()).SequenceEqual(Enumerable.Range(1, budget))
                    && f.Http.Messages.All(m => m["msg_id"]?.GetValue<string>() == "first"), "sequence or long C2C window lost");
            }
        });
        await test("runtime delivery rejects stale future malformed and replayed ingress across restart", () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); f.Receive("first");
            f.Receive("old", clock.Now.AddMinutes(-5)); f.Receive("future", clock.Now.AddSeconds(1));
            f.Dispatch("GROUP_AT_MESSAGE_CREATE", new() { ["id"] = "bad-time", ["group_openid"] = "group-A",
                ["author"] = new JsonObject { ["member_openid"] = "member-A" }, ["timestamp"] = "bad" });
            Require(f.Inbound.Count == 1, "unusable trigger reached host");
            f.Restart(); f.Receive("first"); Require(f.Inbound.Count == 1, "restart replay reached host");
            return Task.CompletedTask;
        });
        await test("runtime delivery persists attempt before transport and final receipt afterward", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.Http.BeforeMessage = _ =>
            {
                Require(f.Rows().Single()["State"]!.GetValue<int>() == (int)OfficialDeliveryState.Attempting, "HTTP preceded durable attempt");
                return Task.CompletedTask;
            };
            var result = await f.Send(); var row = f.Rows().Single();
            Require(result.Ok && result.MessageId > 0 && row["State"]!.GetValue<int>() == (int)OfficialDeliveryState.Completed
                && row["MessageAlias"]!.GetValue<long>() == result.MessageId, "final scoped receipt not durable");
            var snapshot = File.ReadAllText(f.LedgerPath);
            Require(!snapshot.Contains("synthetic-output") && !snapshot.Contains("group-A") && !snapshot.Contains("synthetic-token"), "private payload entered ledger");
        });
        await test("runtime delivery unknown transport outcome is terminal and never active retry", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.Http.BeforeMessage = _ => throw new HttpRequestException("synthetic-body");
            var result = await f.Send(); var row = f.Rows().Single();
            Require(result.Delivery?.Status == DeliveryStatus.Unknown && f.Http.Messages.Count == 1
                && row["Status"]!.GetValue<int>() == (int)DeliveryStatus.Unknown, "unknown outcome retried or lost");
        });
        await test("runtime delivery rechecks source after waiting for transport gate", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            var gate = (SemaphoreSlim)typeof(OfficialBotGateway).GetField("_sendGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Gateway)!;
            await gate.WaitAsync(); Task<SendResult> pending;
            try { pending = f.Send(); await f.Http.TokenSeen.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Current = false; }
            finally { gate.Release(); }
            Require(!(await pending).Ok && f.Http.Messages.Count == 0
                && f.Rows().Single()["State"]!.GetValue<int>() == (int)OfficialDeliveryState.Abandoned, "gate wait retained retired authorization");
        });
        await test("runtime delivery credential retry retains operation and repeats durable attempt", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.Http.MessageResponse = count => count == 1 ? Reply(401, "{\"code\":11244}") : Reply(200, "{\"id\":\"sent\"}");
            f.Http.BeforeMessage = count =>
            {
                Require(f.Rows().Single()["MessageAttempts"]!.GetValue<int>() == count, "retry had no durable attempt");
                return Task.CompletedTask;
            };
            Require((await f.Send()).Ok && f.Http.Tokens == 2 && f.Http.Messages.Count == 2
                && JsonNode.DeepEquals(f.Http.Messages[0], f.Http.Messages[1]) && f.Rows().Count == 1, "credential retry changed operation");
        });
        await test("runtime delivery expiry after credential rejection prevents retry", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.Http.MessageResponse = _ => { clock.Now += TimeSpan.FromMinutes(5); return Reply(401, "{\"code\":11244}"); };
            Require(!(await f.Send()).Ok && f.Http.Messages.Count == 1, "expired retry reached HTTP");
        });
        await test("runtime delivery upload and final message share original trigger and reservation", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.Http.BeforeUpload = () =>
            {
                Require(f.Rows().Single()["State"]!.GetValue<int>() == (int)OfficialDeliveryState.Uploading, "upload preceded durable attempt");
                f.Receive("second"); return Task.CompletedTask;
            };
            Require(await f.Gateway.SendImageAsync(true, f.Alias, [1, 2, 3]), "media failed");
            var row = f.Rows().Single();
            Require(f.Http.Uploads == 1 && f.Http.Messages.Single()["msg_id"]!.GetValue<string>() == "first"
                && row["MessageAttempts"]!.GetValue<int>() == 1 && row["Upload"]!["Attempts"]!.GetValue<int>() == 1,
                "media borrowed later trigger or consumed a separate reservation");
        });
        await test("runtime delivery group removal during upload blocks final message", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.Http.BeforeUpload = () => { f.Dispatch("GROUP_DEL_ROBOT", new() { ["group_openid"] = "group-A" }); return Task.CompletedTask; };
            Require(!await f.Gateway.SendImageAsync(true, f.Alias, [1, 2, 3]) && f.Http.Uploads == 1 && f.Http.Messages.Count == 0,
                "removed relationship still delivered media");
        });
        await test("runtime delivery text and media consume one shared trigger budget", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            for (var i = 0; i < 4; i++) Require((await f.Send()).Ok, "text reservation failed");
            Require(await f.Gateway.SendImageAsync(true, f.Alias, [1, 2, 3]), "fifth shared reservation failed");
            Require(!await f.Gateway.SendImageAsync(true, f.Alias, [1, 2, 3]) && f.Http.Uploads == 1 && f.Http.Messages.Count == 5,
                "media started upload after shared budget was exhausted");
        });
        await test("runtime delivery source retirement during persistence still prevents HTTP", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.AfterCommit = snapshot =>
            {
                if (States(snapshot).Contains(OfficialDeliveryState.Attempting)) f.Current = false;
            };
            Require(!(await f.Send()).Ok && f.Http.Messages.Count == 0, "durable write hid source retirement");
        });
        await test("runtime delivery ambiguous attempt write grants no HTTP and recovers unknown", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.AfterCommit = snapshot =>
            {
                if (States(snapshot).Contains(OfficialDeliveryState.Attempting)) throw new IOException("synthetic-publication-failure");
            };
            Require(!(await f.Send()).Ok && f.Http.Messages.Count == 0, "failed attempt publication granted HTTP");
            f.AfterCommit = null; f.Restart(); f.Receive("first");
            Require(f.Inbound.Count == 1 && f.Rows().Single()["Status"]!.GetValue<int>() == (int)DeliveryStatus.Unknown,
                "ambiguous attempt resumed after restart");
        });
        await test("runtime delivery failed final receipt cannot encourage another send", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.AfterCommit = snapshot =>
            {
                if (States(snapshot).Contains(OfficialDeliveryState.Completed)) throw new IOException("synthetic-receipt-failure");
            };
            var sent = await f.Send();
            Require(sent.Delivery?.Status == DeliveryStatus.Unknown && f.Http.Messages.Count == 1, "receipt failure became retryable failure");
            Require(!(await f.Send()).Ok && f.Http.Messages.Count == 1, "retired ledger sent again");
        });
        await test("runtime delivery media keeps an unknown final message outcome", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.Http.BeforeMessage = _ => throw new HttpRequestException("synthetic-final-message-failure");
            var result = await ((IQqChatSource)f.Gateway).SendImageWithReceiptAsync(true, f.Alias, [1, 2, 3]);
            Require(result.Delivery?.Status == DeliveryStatus.Unknown && f.Http.Uploads == 1 && f.Http.Messages.Count == 1,
                "upload success hid unknown final message outcome");
        });
    }

    private static IEnumerable<OfficialDeliveryState> States(string snapshot)
        => JsonNode.Parse(snapshot)!["Operations"]!.AsObject().Select(p => (OfficialDeliveryState)p.Value!["State"]!.GetValue<int>());

    private static HttpResponseMessage Reply(int status, string body)
        => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private sealed class Owner : IPlatformSourceOwner
    { public string? ConversationKey(QqChatMessage message, Func<string, bool> exists) => null; }
    private sealed class Fixture : IDisposable
    {
        private readonly SyntheticDirectory _directory = new();
        private readonly FrozenClock _clock;
        private readonly bool _group;
        private OfficialDeliveryLedger? _ledger;
        public OfficialIdentityMap Ids { get; }
        public OfficialBotGateway Gateway { get; private set; } = null!;
        public RuntimeHttp Http { get; } = new();
        public Action<string>? AfterCommit { get; set; }
        public bool Current { get; set; } = true;
        public List<QqChatMessage> Inbound { get; } = [];
        public List<string> Logs { get; } = [];
        public string LedgerPath => _directory.Map + ".delivery";
        public long Alias { get; }
        public ConversationId Target => new(PlatformId.QqOfficial, AccountScope.Legacy,
            _group ? ConversationKind.GroupChat : ConversationKind.PrivateChat, Alias.ToString());
        public Fixture(FrozenClock clock, bool group = true)
        {
            _clock = clock; _group = group; Ids = new(_directory.Map);
            Alias = Ids.AliasFor(new(new("app-A", group ? OfficialIdentityKind.Group : OfficialIdentityKind.C2cUser), group ? "group-A" : "user-A"));
            Open();
        }
        private void Open()
        {
            var box = new PlatformSettingsBox(new PlatformOptions { OfficialEnabled = true, OfficialAppId = "app-A",
                OfficialAppSecret = "synthetic", OfficialApiBase = "https://example.com", OfficialTokenUrl = "https://example.com/token" });
            Func<BotAgent.Domain.Ports.IOfficialIdentityMap> identities = () => Ids;
            Func<OfficialDeliveryLedger> delivery = () => _ledger = new(new ObservedSnapshot(new OfficialDeliverySnapshotFile(LedgerPath),
                snapshot => AfterCommit?.Invoke(snapshot)), "app-A", () => _clock.Now);
            Action<string> log = Logs.Add;
            // The retained pre-integration assembly uses the four-argument constructor.
            var constructor = typeof(OfficialBotGateway).GetConstructors().Single();
            Gateway = (OfficialBotGateway)constructor.Invoke(constructor.GetParameters().Length == 5
                ? [box, Http, identities, log, delivery] : [box, Http, identities, log]);
            Gateway.MessageReceived += Inbound.Add;
        }
        public OfficialIdentityKey MessageKey(string id, OfficialMessageDirection direction = OfficialMessageDirection.Inbound)
            => new(new("app-A", OfficialIdentityKind.Message,
                new(PlatformId.QqOfficial, "app-A", _group ? ConversationKind.GroupChat : ConversationKind.PrivateChat,
                    _group ? "group-A" : "user-A"), direction), id);
        public MessageRef Ref(string id) => new(Target,
            (Inbound.LastOrDefault(m => Ids.OriginalOf(m.MessageId, MessageKey(id).Scope) == id)?.MessageId
                ?? Ids.AliasFor(MessageKey(id))).ToString());
        public PlatformSourceLease Lease(string id) => new(new Owner(), Gateway,
            new(new(PlatformId.QqOfficial, AccountScope.Legacy, "synthetic-driver"), Ref(id)), CancellationToken.None, () => Current);
        public Task<SendResult> Send() => Gateway.SendTextAsync(_group, Alias, "synthetic-output");
        public Task<DeliveryResult> Recall(long message) => ((IQqChatSource)Gateway).RecallOwnMessageAsync(_group, Alias, message);
        public void Receive(string id, DateTimeOffset? at = null) => Dispatch(_group ? "GROUP_AT_MESSAGE_CREATE" : "C2C_MESSAGE_CREATE",
            new() { ["id"] = id, ["group_openid"] = _group ? "group-A" : null,
                ["author"] = new JsonObject { [_group ? "member_openid" : "user_openid"] = _group ? "member-A" : "user-A" },
                ["timestamp"] = (at ?? _clock.Now).ToString("O"), ["content"] = "synthetic-input" });
        public void Dispatch(string kind, JsonObject data) => typeof(OfficialBotGateway)
            .GetMethod("HandleDispatch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Gateway, [kind, data, null]);
        public List<JsonObject> Rows() => JsonNode.Parse(File.ReadAllText(LedgerPath))!["Operations"]!.AsObject()
            .Select(p => p.Value!.AsObject()).ToList();
        public void Restart() { Gateway.Dispose(); _ledger?.Dispose(); _ledger = null; Open(); }
        public void Dispose() { Gateway.Dispose(); _ledger?.Dispose(); _directory.Dispose(); }
    }
    private sealed class ObservedSnapshot(IOfficialDeliverySnapshot inner, Action<string> afterCommit) : IOfficialDeliverySnapshot
    {
        public string? Load() => inner.Load();
        public void Commit(string snapshot) { inner.Commit(snapshot); afterCommit(snapshot); }
        public void Dispose() => inner.Dispose();
    }
    private sealed class RuntimeHttp : IPlatformHttpFetcher
    {
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);
        public int Tokens { get; private set; }
        public int Uploads { get; private set; }
        public List<JsonObject> Messages { get; } = [];
        public TaskCompletionSource TokenSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<int, Task>? BeforeMessage { get; set; }
        public Func<Task>? BeforeUpload { get; set; }
        public Func<HttpContent, CancellationToken, Task>? ReadUpload { get; set; }
        public Func<int, HttpResponseMessage>? UploadResponse { get; set; }
        public Func<int, HttpResponseMessage>? MessageResponse { get; set; }
        public List<string> Recalls { get; } = [];
        public Func<int, Task>? BeforeRecall { get; set; }
        public Func<int, HttpResponseMessage>? RecallResponse { get; set; }
        public Func<Uri, HttpResponseMessage>? DownloadResponse { get; set; }
        public List<Uri> Downloads { get; } = [];
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        { Tokens++; TokenSeen.TrySetResult(); return Task.FromResult(Reply(200, "{\"access_token\":\"synthetic-token\",\"expires_in\":7200}")); }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default)
            => GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public async Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default)
        {
            var uri = new Uri(url, UriKind.Absolute); Downloads.Add(uri);
            var response = DownloadResponse?.Invoke(uri) ?? throw new InvalidOperationException("unexpected_request");
            try
            {
                // Match HttpClient: its timeout covers buffering, but ends at headers for streaming calls.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Timeout);
                if (option == HttpCompletionOption.ResponseContentRead) await response.Content.LoadIntoBufferAsync(timeout.Token);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url.ToString(), option, ct);
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion,
            CancellationToken ct = default)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
            { Tokens++; TokenSeen.TrySetResult(); return Reply(200, "{\"access_token\":\"synthetic-token\",\"expires_in\":7200}"); }
            if (request.Method == HttpMethod.Delete)
            {
                Require(request.Content is null, "recall request unexpectedly has a body");
                Recalls.Add(request.RequestUri.AbsolutePath);
                if (BeforeRecall is not null) await BeforeRecall(Recalls.Count);
                return RecallResponse?.Invoke(Recalls.Count) ?? Reply(200, string.Empty);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/files"))
            {
                Uploads++;
                if (ReadUpload is not null) await ReadUpload(request.Content!, ct);
                if (BeforeUpload is not null) await BeforeUpload();
                return UploadResponse?.Invoke(Uploads) ?? Reply(200, "{\"file_info\":\"synthetic-media\",\"ttl\":60}");
            }
            Messages.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            if (BeforeMessage is not null) await BeforeMessage(Messages.Count);
            return MessageResponse?.Invoke(Messages.Count) ?? Reply(200, "{\"id\":\"sent\"}");
        }
    }
}
