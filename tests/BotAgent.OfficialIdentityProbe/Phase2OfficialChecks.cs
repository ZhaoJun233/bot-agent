using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Official;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Platforms.Net;
using BotAgent.Services;
using BotAgent.Services.Official;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

internal static class Phase2OfficialChecks
{
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        await test("phase2 official: opt-in intents and automatic downgrade to 1<<25 on close 4009", () =>
        {
            using var dir = new SyntheticDirectory();
            var settingsJson = JsonDocument.Parse("{\"enableInteractiveMarkdown\":true,\"subscribeMemberEvents\":true}");
            var dict = settingsJson.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var box = new SettingsBox(new AppSettings
            {
                OfficialEnabled = true,
                OfficialAppId = "app-intents",
                OfficialAppSecret = "sec-intents",
                OfficialApiBase = "https://official.example.com"
            });
            using var gw = new OfficialBotGateway(
                box,
                new InteractionHttp(),
                () => new OfficialIdentityMap(dir.Map),
                _ => { },
                () => new OfficialDeliveryLedger(new OfficialDeliverySnapshotFile(Path.Combine(dir.Root, "ledger.json")), "app-intents"))
                .BindPluginSettings(() => dict);

            const int baseIntents = 1 << 25;
            const int expectedOptIn = (1 << 25) | (1 << 26) | (1 << 24);
            Require(gw.CurrentIdentifyIntents == expectedOptIn && !gw.IsExtraIntentsDowngraded,
                $"expected opt-in intents {expectedOptIn}, got {gw.CurrentIdentifyIntents}");

            gw.ObserveWebSocketCloseCode(4009);
            Require(gw.IsExtraIntentsDowngraded && gw.CurrentIdentifyIntents == baseIntents,
                $"expected automatic downgrade to {baseIntents} after 4009, got {gw.CurrentIdentifyIntents}");
            return Task.CompletedTask;
        });

        await test("phase2 official: INTERACTION_CREATE sends single PUT ACK, blocks // commands, and Markdown 11253 falls back to plain text", async () =>
        {
            using var dir = new SyntheticDirectory();
            var settingsJson = JsonDocument.Parse("{\"enableInteractiveMarkdown\":true,\"subscribeMemberEvents\":true}");
            var dict = settingsJson.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var http = new InteractionHttp { FailMarkdownWith11253 = true };
            var box = new SettingsBox(new AppSettings
            {
                OfficialEnabled = true,
                OfficialAppId = "app-inter",
                OfficialAppSecret = "sec-inter",
                OfficialApiBase = "https://official.example.com",
                OfficialTokenUrl = "https://official.example.com/token"
            });
            var ids = new OfficialIdentityMap(dir.Map);
            using var gw = new OfficialBotGateway(
                box,
                http,
                () => ids,
                _ => { },
                () => new OfficialDeliveryLedger(new OfficialDeliverySnapshotFile(Path.Combine(dir.Root, "ledger.json")), "app-inter"))
                .BindPluginSettings(() => dict);

            var inbound = new List<QqChatMessage>();
            gw.MessageReceived += inbound.Add;
            var dispatch = typeof(OfficialBotGateway).GetMethod("HandleDispatch", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var interApprove = new JsonObject
            {
                ["id"] = "inter-approve-1",
                ["chat_type"] = 1,
                ["group_openid"] = "grp-open-1",
                ["group_member_openid"] = "mem-open-1",
                ["timestamp"] = clock.Now.ToString("O"),
                ["data"] = new JsonObject { ["resolved"] = new JsonObject { ["button_data"] = "同意 AB23CD" } }
            };
            dispatch.Invoke(gw, ["INTERACTION_CREATE", interApprove, null]);
            dispatch.Invoke(gw, ["INTERACTION_CREATE", interApprove, null]); // duplicate!
            await http.FirstAckSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Require(inbound.Count == 1 && http.Acks.Count == 1 && http.Acks[0] == "/interactions/inter-approve-1",
                $"expected single ACK and single inbound event, got acks={http.Acks.Count} inbound={inbound.Count}");
            Require(inbound[0].IsReliableIdentity == true && inbound[0].AllowsCommands && inbound[0].Text == "同意 AB23CD",
                "approval button interaction should preserve reliable operator identity and allow ApprovalUseCase re-verification");

            var interSlash = new JsonObject
            {
                ["id"] = "inter-slash-2",
                ["chat_type"] = 1,
                ["group_openid"] = "grp-open-1",
                ["group_member_openid"] = "mem-open-1",
                ["timestamp"] = clock.Now.ToString("O"),
                ["data"] = new JsonObject { ["resolved"] = new JsonObject { ["button_data"] = "//status" } }
            };
            dispatch.Invoke(gw, ["INTERACTION_CREATE", interSlash, null]);
            Require(inbound.Count == 2 && !inbound[1].AllowsCommands,
                "button_data starting with // must never allow explicit agent commands");

            var interGuild = new JsonObject
            {
                ["id"] = "inter-guild-3",
                ["chat_type"] = 0,
                ["group_openid"] = "grp-open-1",
                ["group_member_openid"] = "mem-open-1",
                ["timestamp"] = clock.Now.ToString("O"),
                ["data"] = new JsonObject { ["resolved"] = new JsonObject { ["button_data"] = "同意 AB23CD" } }
            };
            dispatch.Invoke(gw, ["INTERACTION_CREATE", interGuild, null]);
            var interSpoofActor = new JsonObject
            {
                ["id"] = "inter-spoof-4",
                ["chat_type"] = 1,
                ["group_openid"] = "grp-open-1",
                ["timestamp"] = clock.Now.ToString("O"),
                ["data"] = new JsonObject { ["resolved"] = new JsonObject { ["user_id"] = "mem-spoof", ["button_data"] = "同意 AB23CD" } }
            };
            dispatch.Invoke(gw, ["INTERACTION_CREATE", interSpoofActor, null]);
            Require(inbound.Count == 2,
                "Guild chat_type=0 and data.resolved.user_id spoof without top-level actor must be rejected");

            // File attachment + empty asr_refer_text voice attachment extraction
            var fileAndVoiceMsg = new JsonObject
            {
                ["id"] = "msg-media-3",
                ["group_openid"] = "grp-open-1",
                ["author"] = new JsonObject { ["member_openid"] = "mem-open-1" },
                ["timestamp"] = clock.Now.ToString("O"),
                ["content"] = "请看附件和语音",
                ["attachments"] = new JsonArray
                {
                    new JsonObject { ["content_type"] = "voice", ["voice_wav_url"] = "https://cdn.example.com/a.wav" },
                    new JsonObject { ["content_type"] = "application/pdf", ["filename"] = "guide.pdf", ["url"] = "https://cdn.example.com/guide.pdf", ["size"] = 2048 }
                }
            };
            dispatch.Invoke(gw, ["GROUP_AT_MESSAGE_CREATE", fileAndVoiceMsg, null]);
            Require(inbound.Count == 3 && !inbound[2].AllowsCommands
                && inbound[2].VoiceAttachments?.Single().WavUrl == "https://cdn.example.com/a.wav"
                && inbound[2].VoiceAttachments![0].Transcript is null
                && inbound[2].FileAttachments?.Single().FileName == "guide.pdf",
                "inbound voice without asr_refer_text and PDF attachment were not both extracted safely");

            // Opt-in Markdown 11253 fallback to msg_type = 0 plain text
            var groupAlias = inbound[2].GroupId;
            var groupName = await gw.GetGroupNameAsync(groupAlias);
            Require(groupName == "合成官方群" && http.GetPaths.Contains("/v2/groups/grp-open-1/info"),
                $"GetGroupNameAsync should query /v2/groups/{{groupOpenId}}/info, got name={groupName} paths={string.Join(",", http.GetPaths)}");
            Require(gw.SupportsStructuredMarkdown(Channels.Official),
                "SupportsStructuredMarkdown should be true before permission denial");
            var targetConv = new ConversationId(PlatformId.QqOfficial, AccountScope.Legacy, ConversationKind.GroupChat, groupAlias.ToString());
            var lease = new PlatformSourceLease(
                new DummyOwner(),
                gw,
                new(new(PlatformId.QqOfficial, AccountScope.Legacy, "official-driver"), new MessageRef(targetConv, inbound[2].MessageId.ToString())),
                CancellationToken.None,
                () => true);
            using (PlatformSourceExecution.Enter(lease))
            {
                var sent = await gw.SendTextAsync(true, groupAlias, "## 标题\n```txt\nhello\n```");
                Require(sent.Ok && http.SentMessages.Count == 2
                    && http.SentMessages[0]["msg_type"]?.GetValue<int>() == 2
                    && http.SentMessages[1]["msg_type"]?.GetValue<int>() == 0
                    && sent.Delivery is { Status: DeliveryStatus.Degraded, ReasonCode: "markdown_downgraded_to_text", DegradedFrom: "markdown" }
                    && gw.IsMarkdownPermissionDenied
                    && !gw.SupportsStructuredMarkdown(Channels.Official),
                    "11253 on msg_type=2 Markdown must downgrade to Degraded plain text and latch IsMarkdownPermissionDenied");
            }

            // 304080 invalid_parameters falls back cleanly without permanently latching permission_denied
            var http304080 = new InteractionHttp { FailMarkdownErrorCode = 304080 };
            var dir2Path = Path.Combine(dir.Root, "sub-304080");
            Directory.CreateDirectory(dir2Path);
            using var gw2 = new OfficialBotGateway(
                box,
                http304080,
                () => new OfficialIdentityMap(Path.Combine(dir2Path, "ids.json")),
                _ => { },
                () => new OfficialDeliveryLedger(new OfficialDeliverySnapshotFile(Path.Combine(dir2Path, "ledger.json")), "app-inter"))
                .BindPluginSettings(() => dict);
            var inbound2 = new List<QqChatMessage>();
            gw2.MessageReceived += inbound2.Add;
            dispatch.Invoke(gw2, ["GROUP_AT_MESSAGE_CREATE", fileAndVoiceMsg, null]);
            var groupAlias2 = inbound2[0].GroupId;
            var targetConv2 = new ConversationId(PlatformId.QqOfficial, AccountScope.Legacy, ConversationKind.GroupChat, groupAlias2.ToString());
            var lease2 = new PlatformSourceLease(
                new DummyOwner(),
                gw2,
                new(new(PlatformId.QqOfficial, AccountScope.Legacy, "official-driver"), new MessageRef(targetConv2, inbound2[0].MessageId.ToString())),
                CancellationToken.None,
                () => true);
            using (PlatformSourceExecution.Enter(lease2))
            {
                var sent2 = await gw2.SendTextAsync(true, groupAlias2, "## 格式参数回退\n**text**");
                Require(sent2.Ok && http304080.SentMessages.Count == 2
                    && sent2.Delivery is { Status: DeliveryStatus.Degraded, ReasonCode: "markdown_downgraded_to_text" }
                    && !gw2.IsMarkdownPermissionDenied
                    && gw2.SupportsStructuredMarkdown(Channels.Official),
                    "304080 invalid_parameters should degrade cleanly without latching IsMarkdownPermissionDenied");
            }
        });

        await test("phase2 official: WS INTERACTION_CREATE event_id passive reply and IsCommandResponse keyboard guard", async () =>
        {
            using var dir = new SyntheticDirectory();
            var settingsJson = JsonDocument.Parse("{\"enableInteractiveMarkdown\":true,\"subscribeMemberEvents\":false}");
            var dict = settingsJson.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var http = new InteractionHttp();
            var box = new SettingsBox(new AppSettings
            {
                OfficialEnabled = true,
                OfficialAppId = "app-ws-inter",
                OfficialAppSecret = "sec-ws-inter",
                OfficialApiBase = "https://official.example.com",
                OfficialTokenUrl = "https://official.example.com/token"
            });
            var ids = new OfficialIdentityMap(dir.Map);
            using var gw = new OfficialBotGateway(
                box,
                http,
                () => ids,
                _ => { },
                () => new OfficialDeliveryLedger(new OfficialDeliverySnapshotFile(Path.Combine(dir.Root, "ledger.json")), "app-ws-inter"))
                .BindPluginSettings(() => dict);

            var inbound = new List<QqChatMessage>();
            gw.MessageReceived += inbound.Add;

            var handleFrame = typeof(OfficialBotGateway).GetMethod(
                "HandleGatewayDispatchFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            const string fid = "0123456789abcdef0123456789abcdef";
            const string tok = "fedcba9876543210fedcba9876543210";
            var wsFrame = new JsonObject
            {
                ["op"] = 0,
                ["t"] = "INTERACTION_CREATE",
                ["id"] = "ws-evt-900",
                ["d"] = new JsonObject
                {
                    ["id"] = "inter-c2c-900",
                    ["chat_type"] = 2,
                    ["user_openid"] = "user-open-c2c-1",
                    ["timestamp"] = clock.Now.ToString("O"),
                    ["data"] = new JsonObject
                    {
                        ["resolved"] = new JsonObject
                        {
                            ["button_data"] = $"//memory confirm {fid} 1 {tok} allow-source private-only"
                        }
                    }
                }
            };
            await ((Task)handleFrame.Invoke(gw, [wsFrame, CancellationToken.None])!).ConfigureAwait(false);
            Require(inbound.Count == 1 && !inbound[0].IsGroup && inbound[0].AllowsCommands,
                "WS C2C memory button interaction should emit reliable private inbound");

            var c2cAlias = inbound[0].UserId;
            var targetConv = new ConversationId(PlatformId.QqOfficial, AccountScope.Legacy, ConversationKind.PrivateChat, c2cAlias.ToString());
            var lease = new PlatformSourceLease(
                new DummyOwner(),
                gw,
                new(new(PlatformId.QqOfficial, AccountScope.Legacy, "official-driver"), new MessageRef(targetConv, inbound[0].MessageId.ToString())),
                CancellationToken.None,
                () => true);
            var cardPrompt = $"""
                本人偏好：PersonalNote = 测试笔记
                事实 {fid} · 版本 1 · 待确认
                //memory confirm {fid} 1 {tok} allow-source private-only
                """;
            using (PlatformSourceExecution.Enter(lease))
            {
                // Ordinary chat text outside IsCommandResponse must NOT attach memory keyboard
                var chatSent = await gw.SendTextAsync(false, c2cAlias, cardPrompt);
                Require(chatSent.Ok && http.SentMessages.Count == 1
                    && http.SentMessages[0]["event_id"]?.GetValue<string>() == "ws-evt-900"
                    && http.SentMessages[0]["msg_id"] is null
                    && http.SentMessages[0]["keyboard"] is null,
                    $"WS interaction reply must use event_id and suppress keyboard outside IsCommandResponse (payload={http.SentMessages[0].ToJsonString()})");

                // Inside IsCommandResponse, memory keyboard IS attached
                using (BotAgent.Services.Memory.MemoryUseScope.EnterCommandResponse())
                {
                    var cmdSent = await gw.SendTextAsync(false, c2cAlias, cardPrompt);
                    Require(cmdSent.Ok && http.SentMessages.Count == 2
                        && http.SentMessages[1]["keyboard"] is JsonObject,
                        "IsCommandResponse should attach official inline keyboard for private confirmation prompt");
                }
            }
        });
    }

    private sealed class DummyOwner : IPlatformSourceOwner
    {
        public string? ConversationKey(QqChatMessage message, Func<string, bool> exists) => null;
    }

    private sealed class InteractionHttp : IPlatformHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool FailMarkdownWith11253 { get; set; }
        public int? FailMarkdownErrorCode { get; set; }
        public List<string> Acks { get; } = [];
        public List<string> GetPaths { get; } = [];
        public List<JsonObject> SentMessages { get; } = [];
        public TaskCompletionSource FirstAckSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
            => Task.FromResult(Json(HttpStatusCode.OK, "{\"access_token\":\"tok-official-p2\",\"expires_in\":7200}"));

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken ct = default)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                lock (GetPaths) GetPaths.Add(path);
                if (path.EndsWith("/info", StringComparison.Ordinal))
                    return Json(HttpStatusCode.OK, "{\"group_name\":\"合成官方群\"}");
            }
            if (request.Method == HttpMethod.Put && path.StartsWith("/interactions/", StringComparison.Ordinal))
            {
                lock (Acks) Acks.Add(path);
                FirstAckSeen.TrySetResult();
                return Json(HttpStatusCode.OK, "{}");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/messages", StringComparison.Ordinal))
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
                SentMessages.Add(body);
                var mdCode = FailMarkdownErrorCode ?? (FailMarkdownWith11253 ? 11253 : 0);
                if (mdCode != 0 && body["msg_type"]?.GetValue<int>() == 2)
                    return Json(mdCode == 11253 ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest, $"{{\"code\":{mdCode}}}");
                return Json(HttpStatusCode.OK, $"{{\"id\":\"out-{SentMessages.Count}\"}}");
            }
            return Json(HttpStatusCode.OK, "{}");
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
