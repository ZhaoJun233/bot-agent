using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Platforms.Feishu;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Platforms.Net;
using BotAgent.Services;

internal static class Phase2FeishuChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, Action<bool, string> require)
    {
        await test("phase2: AES-256-CBC encrypt challenge succeeds with EncryptKey only and tampered cipher fails 401", async () =>
        {
            const string encKey = "feishu-encrypt-key-synthetic-01";
            var box = new SettingsBox(new AppSettings
            {
                FeishuEnabled = true,
                FeishuAppId = "app-enc",
                FeishuAppSecret = "sec-enc",
                FeishuEncryptKey = encKey,
                FeishuVerificationToken = "",
                FeishuWhitelist = "*"
            });
            using var gw = new FeishuBotGateway(box, new Phase2FakeHttp(), log: null, ids: null, dedupStore: new FeishuWebhookDedupStore());

            var plainChallenge = "{\"type\":\"url_verification\",\"challenge\":\"challenge_aes_only_9001\"}";
            var encryptedBody = new JsonObject { ["encrypt"] = EncryptFeishu(plainChallenge, encKey) }.ToJsonString();
            var ok = await gw.HandleWebhookAsync(encryptedBody, null, null, null);
            require(ok.Handled && ok.StatusCode == 200 && ok.ResponseBody.Contains("challenge_aes_only_9001"),
                $"EncryptKey-only url_verification failed: {ok.StatusCode} {ok.ResponseBody}");

            var unauthenticated = await gw.HandleWebhookAsync(plainChallenge, null, null, null);
            require(!unauthenticated.Handled && unauthenticated.StatusCode == 401 && unauthenticated.ResponseBody.Contains("encrypted_payload_required"),
                $"unencrypted unsigned challenge when EncryptKey is set must fail 401 encrypted_payload_required, got {unauthenticated.StatusCode} {unauthenticated.ResponseBody}");

            var badCipher = new JsonObject { ["encrypt"] = Convert.ToBase64String(new byte[32]) }.ToJsonString();
            var badRes = await gw.HandleWebhookAsync(badCipher, null, null, null);
            var badJsonCipher = new JsonObject { ["encrypt"] = EncryptFeishu("{not-valid-json", encKey) }.ToJsonString();
            var badJsonRes = await gw.HandleWebhookAsync(badJsonCipher, null, null, null);
            require(!badRes.Handled && badRes.StatusCode == 401 && badRes.ResponseBody.Contains("unauthorized")
                && badRes.ResponseBody == badJsonRes.ResponseBody,
                $"unsigned tampered/bad-json cipher must fail uniform 401 unauthorized, got {badRes.StatusCode} {badRes.ResponseBody} / {badJsonRes.ResponseBody}");

            InboundMessage? captured = null;
            gw.InboundReceived += m => captured = m;
            var innerEvent = new JsonObject
            {
                ["header"] = new JsonObject { ["event_id"] = "evt-aes-msg-1", ["event_type"] = "im.message.receive_v1" },
                ["event"] = new JsonObject
                {
                    ["sender"] = new JsonObject { ["sender_id"] = new JsonObject { ["open_id"] = "ou_alice" } },
                    ["message"] = new JsonObject
                    {
                        ["message_id"] = "om_aes_1",
                        ["chat_id"] = "oc_aes_group",
                        ["chat_type"] = "group",
                        ["content"] = "{\"text\":\"加密事件正文\"}"
                    }
                }
            }.ToJsonString();
            var encEventBody = new JsonObject { ["encrypt"] = EncryptFeishu(innerEvent, encKey) }.ToJsonString();
            var ts = Clock.UtcNow.ToUnixTimeSeconds().ToString();
            const string nonce = "nonce-aes-evt-1";
            var sig = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ts + nonce + encKey + encEventBody)));
            var evtRes = await gw.HandleWebhookAsync(encEventBody, sig, ts, nonce);
            require(evtRes.StatusCode == 200 && captured is not null && captured.Text == "加密事件正文",
                "signed encrypted event did not decrypt and dispatch");
        });

        await test("phase2: botOpenId distinguishes @other from @bot and filters app self-messages", async () =>
        {
            var http = new Phase2FakeHttp { BotOpenId = "ou_bot_self" };
            var box = new SettingsBox(new AppSettings
            {
                FeishuEnabled = true,
                FeishuAppId = "app-mention",
                FeishuAppSecret = "sec-mention",
                FeishuVerificationToken = "tok-mention",
                FeishuApiBase = "https://feishu.example.com",
                FeishuWhitelist = "*"
            });
            using var gw = new FeishuBotGateway(box, http, log: null, ids: null, dedupStore: new FeishuWebhookDedupStore());
            var received = new List<InboundMessage>();
            gw.InboundReceived += received.Add;

            var mentionOther = BuildEvent("evt-at-other", "om-at-other", "text", "{\"text\":\"@_user_1 请看下方案\"}",
                mentions: [new JsonObject { ["key"] = "@_user_1", ["name"] = "群友B", ["id"] = new JsonObject { ["open_id"] = "ou_member_b" } }]);
            var r1 = await gw.HandleWebhookAsync(mentionOther, null, null, null);
            require(r1.StatusCode == 200 && received.Count == 1 && !received[0].IsMentioned && received[0].Text == "@群友B 请看下方案",
                $"@other should not mark MentionedSelf, got IsMentioned={received[0].IsMentioned} text={received[0].Text}");

            var mentionBot = BuildEvent("evt-at-bot", "om-at-bot", "text", "{\"text\":\"@_user_1 帮我总结一下\"}",
                mentions: [new JsonObject { ["key"] = "@_user_1", ["name"] = "Bot", ["id"] = new JsonObject { ["open_id"] = "ou_bot_self" } }]);
            var r2 = await gw.HandleWebhookAsync(mentionBot, null, null, null);
            require(r2.StatusCode == 200 && received.Count == 2 && received[1].IsMentioned && received[1].Text == "帮我总结一下",
                $"@bot should mark MentionedSelf and strip placeholder, got IsMentioned={received[1].IsMentioned} text={received[1].Text}");

            var selfAppMsg = BuildEvent("evt-self-app", "om-self-app", "text", "{\"text\":\"bot echo\"}", senderType: "app");
            var r3 = await gw.HandleWebhookAsync(selfAppMsg, null, null, null);
            require(r3.StatusCode == 200 && r3.ResponseBody.Contains("self_ignored") && received.Count == 2,
                "app sender_type must be filtered out as self_ignored");
        });

        await test("phase2: rich post/image/file/audio parsing, resource download, quote lookup and group name negative cache", async () =>
        {
            var http = new Phase2FakeHttp
            {
                BotOpenId = "ou_bot_self",
                ResourceBytes = Encoding.UTF8.GetBytes("synthetic-resource-payload"),
                QuotedText = "被引用历史原文"
            };
            var box = new SettingsBox(new AppSettings
            {
                FeishuEnabled = true,
                FeishuAppId = "app-rich",
                FeishuAppSecret = "sec-rich",
                FeishuVerificationToken = "tok-mention",
                FeishuApiBase = "https://feishu.example.com",
                FeishuWhitelist = "*"
            });
            using var gw = new FeishuBotGateway(box, http, log: null, ids: null, dedupStore: new FeishuWebhookDedupStore());
            var list = new List<InboundMessage>();
            gw.InboundReceived += list.Add;

            var postContent = new JsonObject
            {
                ["zh_cn"] = new JsonObject
                {
                    ["title"] = "版本发布说明",
                    ["content"] = new JsonArray
                    {
                        new JsonArray
                        {
                            new JsonObject { ["tag"] = "at", ["user_id"] = "ou_bot_self", ["user_name"] = "Bot" },
                            new JsonObject { ["tag"] = "text", ["text"] = "请查阅 " },
                            new JsonObject { ["tag"] = "a", ["text"] = "变更单", ["href"] = "https://docs.example.com/1" },
                            new JsonObject { ["tag"] = "img", ["image_key"] = "img_v2_001" }
                        }
                    }
                }
            }.ToJsonString();
            await gw.HandleWebhookAsync(BuildEvent("evt-post-1", "om-post-1", "post", postContent), null, null, null);
            require(list.Count == 1 && list[0].IsMentioned && list[0].Text.Contains("版本发布说明")
                && list[0].Text.Contains("变更单 (https://docs.example.com/1)")
                && list[0].ImageUrls?.Single() == "feishu-media://om-post-1/img_v2_001",
                $"post parse mismatch: {list[0].Text}");

            await gw.HandleWebhookAsync(BuildEvent("evt-file-1", "om-file-1", "file", "{\"file_key\":\"file_k1\",\"file_name\":\"report.pdf\"}"), null, null, null);
            require(list.Count == 2 && !list[1].AllowsCommands && list[1].FileAttachments?.Single().FileName == "report.pdf",
                "file message must attach InboundFileAttachment and disable AllowsCommands");

            await gw.HandleWebhookAsync(BuildEvent("evt-audio-1", "om-audio-1", "audio", "{\"file_key\":\"aud_k1\",\"duration\":4200}"), null, null, null);
            require(list.Count == 3 && !list[2].AllowsCommands && list[2].VoiceAttachments?.Single().DurationSeconds == 4,
                "audio message must attach InboundVoiceAttachment and disable AllowsCommands");

            await gw.StartAsync();
            var mediaBytes = await gw.DownloadMediaUriAsync("feishu-media://om-post-1/img_v2_001");
            var (fileBytes, _, fileName) = await gw.ResolveInboundFileAsync(list[1].FileAttachments![0], true, 0, 0);
            var (voiceBytes, _, _) = await gw.ResolveInboundVoiceAsync(list[2].VoiceAttachments![0], 0);
            require(mediaBytes is not null && fileBytes is not null && voiceBytes is not null && fileName == "report.pdf",
                "resource download failed");

            var (qText, qSender) = await gw.GetQuotedMessageAsync(
                new MessageRef(new ConversationId(PlatformId.Feishu, AccountScope.Default, ConversationKind.GroupChat, "oc_synthetic"), "om_parent_1"));
            require(qText == "被引用历史原文" && qSender == "ou_quoted_sender", $"quote lookup mismatch: {qText}");

            http.ChatInfoStatus = HttpStatusCode.Forbidden;
            var groupAlias = gw.AliasFor("oc_synthetic");
            // Map group alias explicitly via group kind
            var ids = new FeishuIdMap();
            using var gwGroup = new FeishuBotGateway(box, http, log: null, ids: ids, dedupStore: new FeishuWebhookDedupStore());
            await gwGroup.StartAsync();
            var grpId = ids.AliasFor("app-rich", "group", "oc_neg_chat");
            var name1 = await gwGroup.GetGroupNameAsync(grpId);
            var callsAfterFirst = http.ChatInfoCalls;
            var name2 = await gwGroup.GetGroupNameAsync(grpId);
            require(name1 is null && name2 is null && http.ChatInfoCalls == callsAfterFirst,
                "403 group info failure must be negatively cached without repeated HTTP calls");
        });

        await test("phase2: thread reply_in_thread, Schema 2.0 Markdown card, image upload and own-message recall", async () =>
        {
            var http = new Phase2FakeHttp();
            var box = new SettingsBox(new AppSettings
            {
                FeishuEnabled = true,
                FeishuAppId = "app-out",
                FeishuAppSecret = "sec-out",
                FeishuVerificationToken = "tok-mention",
                FeishuApiBase = "https://feishu.example.com",
                FeishuWhitelist = "*"
            });
            var ids = new FeishuIdMap();
            using var gw = new FeishuBotGateway(box, http, log: null, ids: ids, dedupStore: new FeishuWebhookDedupStore());
            await gw.StartAsync();

            var threadedEvt = BuildEvent("evt-thread-1", "om-thread-msg", "text", "{\"text\":\"话题内提问\"}",
                rootId: "om-thread-root", threadId: "omt-topic-1");
            await gw.HandleWebhookAsync(threadedEvt, null, null, null);

            var groupAlias = ids.AliasFor("app-out", "group", "oc_synthetic");
            var replyAlias = ids.AliasFor("app-out", "message", "om-thread-msg");
            var mdReply = "## 诊断结论\n```json\n{\"ok\":true}\n```";
            var sendRes = await gw.SendTextAsync(true, groupAlias, mdReply, replyToMessageId: replyAlias);
            require(sendRes.Ok && http.SentMessages.Count == 1, "threaded markdown card send failed");
            var sentPayload = http.SentMessages[0].Body;
            require(http.SentMessages[0].Path.EndsWith("/om-thread-msg/reply")
                && sentPayload["reply_in_thread"]?.GetValue<bool>() == true
                && sentPayload["msg_type"]?.GetValue<string>() == "interactive"
                && sentPayload["content"]!.GetValue<string>().Contains("\"schema\":\"2.0\""),
                $"expected threaded interactive Schema 2.0 card, got {sentPayload.ToJsonString()}");

            var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
            var imgSent = await gw.SendImageAsync(true, groupAlias, pngBytes, replyToMessageId: replyAlias);
            require(imgSent && http.ImageUploads == 1 && http.SentMessages.Last().Body["msg_type"]?.GetValue<string>() == "image",
                "Feishu SendImageAsync did not upload and send image");

            var foreignAlias = ids.AliasFor("app-out", "message", "om-foreign-not-mine");
            var foreignRecall = await gw.RecallOwnMessageAsync(true, groupAlias, foreignAlias);
            require(!foreignRecall.IsSuccess && foreignRecall.ReasonCode == "not_own_message" && http.Recalls == 0,
                "foreign message recall must be blocked before HTTP");

            var ownRecall = await gw.RecallOwnMessageAsync(true, groupAlias, sendRes.MessageId);
            require(ownRecall.IsSuccess && http.Recalls == 1, "own message recall failed");
        });

        await test("phase2: pbbp2 LongConnection codec, out-of-order fragment reassembly, <3s ACK and shared dedup", async () =>
        {
            var http = new Phase2FakeHttp();
            var settingsJson = JsonDocument.Parse("{\"transportMode\":\"long_connection\",\"enableInteractiveCards\":true}");
            var settingsDict = settingsJson.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var session = new InMemoryFeishuWsSession();
            var dedup = new RecoverablePhase2Dedup();

            var box = new SettingsBox(new AppSettings
            {
                FeishuEnabled = true,
                FeishuAppId = "app-ws",
                FeishuAppSecret = "sec-ws",
                FeishuApiBase = "https://feishu.example.com",
                FeishuWhitelist = "*"
            });
            using var gw = new FeishuBotGateway(
                box,
                http,
                log: null,
                ids: new FeishuIdMap(),
                dedupStore: dedup,
                readPluginSettings: () => settingsDict,
                wsConnect: (_, _) => Task.FromResult<IFeishuWsSession>(session));

            require(gw.IsConfigured && gw.IsLongConnectionMode, "LongConnection should be configured with AppId+AppSecret alone");
            var received = new ConcurrentQueue<InboundMessage>();
            gw.InboundReceived += received.Enqueue;

            await gw.StartAsync();
            await session.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            require(gw.IsConnected, "gateway IsConnected must be true once pbbp2 session opens");

            var fullEventBytes = Encoding.UTF8.GetBytes(BuildEvent("evt-ws-frag-1", "om-ws-frag-1", "text", "{\"text\":\"长连接分片消息\"}"));
            var cut1 = fullEventBytes.Length / 3;
            var cut2 = (fullEventBytes.Length * 2) / 3;
            var parts = new[]
            {
                (Seq: 0, Data: fullEventBytes[..cut1]),
                (Seq: 2, Data: fullEventBytes[cut2..]),
                (Seq: 1, Data: fullEventBytes[cut1..cut2])
            };

            // First test transient 503 dedup failure -> ACK code 500 without burning dedup key
            dedup.Fail = true;
            foreach (var p in parts)
            {
                session.EnqueueServerFrame(new FeishuWsFrame(
                    101, 202, 77, FeishuWsFrameCodec.MethodData,
                    [
                        new FeishuWsHeader("type", "event"),
                        new FeishuWsHeader("message_id", "ws-msg-frag-fail"),
                        new FeishuWsHeader("sum", "3"),
                        new FeishuWsHeader("seq", p.Seq.ToString())
                    ],
                    null, "application/json", p.Data));
            }
            var failAck = await session.NextClientFrameAsync(TimeSpan.FromSeconds(5));
            require(Encoding.UTF8.GetString(failAck.Payload).Contains("500") && received.IsEmpty,
                "transient dedup failure must return 500 ACK without emitting");

            // Now repair dedup and replay same event -> succeeds with 200 ACK + biz_rt, and duplicate replay is suppressed
            dedup.Fail = false;
            foreach (var p in parts)
            {
                session.EnqueueServerFrame(new FeishuWsFrame(
                    102, 203, 77, FeishuWsFrameCodec.MethodData,
                    [
                        new FeishuWsHeader("type", "event"),
                        new FeishuWsHeader("message_id", "ws-msg-frag-ok"),
                        new FeishuWsHeader("sum", "3"),
                        new FeishuWsHeader("seq", p.Seq.ToString())
                    ],
                    null, "application/json", p.Data));
            }
            var okAck = await session.NextClientFrameAsync(TimeSpan.FromSeconds(5));
            require(Encoding.UTF8.GetString(okAck.Payload) == "{\"code\":200}"
                && okAck.GetHeader("biz_rt") is { Length: > 0 }
                && received.TryDequeue(out var wsMsg) && wsMsg.Text == "长连接分片消息",
                "reassembled pbbp2 frame must emit InboundMessage and return 200 ACK with biz_rt");

            session.EnqueueServerFrame(new FeishuWsFrame(
                103, 204, 77, FeishuWsFrameCodec.MethodData,
                [new FeishuWsHeader("type", "event"), new FeishuWsHeader("message_id", "ws-msg-dup")],
                null, "application/json", fullEventBytes));
            var dupAck = await session.NextClientFrameAsync(TimeSpan.FromSeconds(5));
            require(Encoding.UTF8.GetString(dupAck.Payload) == "{\"code\":200}" && received.IsEmpty,
                "duplicate event over pbbp2 must ACK 200 without re-emitting");

            await gw.StopAsync();
            require(!gw.IsConnected, "StopAsync must mark LongConnection disconnected");
        });

        await test("phase2: mention hardening, multi-digit placeholder replace, negative ws varint, and HTTP 400 card fallback", async () =>
        {
            var httpUnknownBot = new Phase2FakeHttp { BotOpenId = null };
            var box = new SettingsBox(new AppSettings
            {
                FeishuEnabled = true,
                FeishuAppId = "app-hard",
                FeishuAppSecret = "sec-hard",
                FeishuVerificationToken = "tok-mention",
                FeishuApiBase = "https://feishu.example.com",
                FeishuWhitelist = "*"
            });
            using var gwUnknown = new FeishuBotGateway(box, httpUnknownBot, log: null, ids: null, dedupStore: new FeishuWebhookDedupStore());
            var recvUnknown = new List<InboundMessage>();
            gwUnknown.InboundReceived += recvUnknown.Add;

            var openIdWhenUnknown = BuildEvent("evt-unk-1", "om-unk-1", "text", "{\"text\":\"@_user_1 测试\"}",
                mentions: [new JsonObject { ["key"] = "@_user_1", ["name"] = "Bot", ["id"] = new JsonObject { ["open_id"] = "ou_bot_self" } }]);
            await gwUnknown.HandleWebhookAsync(openIdWhenUnknown, null, null, null);
            require(recvUnknown.Count == 1 && !recvUnknown[0].IsMentioned,
                "open_id mention when BotOpenId is empty must fail closed");

            var httpKnown = new Phase2FakeHttp { BotOpenId = "ou_bot_self", FailInteractiveCount = 1 };
            var idsKnown = new FeishuIdMap();
            using var gwKnown = new FeishuBotGateway(box, httpKnown, log: null, ids: idsKnown, dedupStore: new FeishuWebhookDedupStore());
            var recvKnown = new List<InboundMessage>();
            gwKnown.InboundReceived += recvKnown.Add;

            var atAll = BuildEvent("evt-all-1", "om-all-1", "text", "{\"text\":\"@_all 通知所有人\"}");
            var atPlain = BuildEvent("evt-plain-1", "om-plain-1", "text", "{\"text\":\"@Bot 假装艾特\"}");
            var multiMention = BuildEvent("evt-multi-1", "om-multi-1", "text", "{\"text\":\"@_user_10 和 @_user_1 请确认\"}",
                mentions:
                [
                    new JsonObject { ["key"] = "@_user_1", ["name"] = "Alice", ["id"] = new JsonObject { ["open_id"] = "ou_alice_m" } },
                    new JsonObject { ["key"] = "@_user_10", ["name"] = "Bob", ["id"] = new JsonObject { ["open_id"] = "ou_bob_m" } }
                ]);
            await gwKnown.HandleWebhookAsync(atAll, null, null, null);
            await gwKnown.HandleWebhookAsync(atPlain, null, null, null);
            await gwKnown.HandleWebhookAsync(multiMention, null, null, null);
            require(recvKnown.Count == 3
                && !recvKnown[0].IsMentioned
                && !recvKnown[1].IsMentioned
                && recvKnown[2].Text == "@Bob 和 @Alice 请确认",
                $"mention hardening/multi-digit failed: all={recvKnown[0].IsMentioned} plain={recvKnown[1].IsMentioned} multi={recvKnown[2].Text}");

            // Negative protobuf varint sign-extension on Service/Method
            var encodedNeg = FeishuWsFrameCodec.Encode(new FeishuWsFrame(1, 2, -1, -2, [], null, null, []));
            require(FeishuWsFrameCodec.TryDecode(encodedNeg, out var decodedNeg) && decodedNeg is { Service: -1, Method: -2 },
                "negative protobuf int32 Service/Method must sign-extend correctly");

            // HTTP 400 with code=230099 falls back from interactive card to plain text
            await gwKnown.StartAsync();
            try
            {
                var cardGroupAlias = idsKnown.AliasFor("app-hard", "group", "oc_synthetic");
                var sendRes = await gwKnown.SendTextAsync(true, cardGroupAlias, "## 卡片标题\n**加粗**");
                require(sendRes.Ok && httpKnown.SentMessages.Count == 2
                    && httpKnown.SentMessages[0].Body["msg_type"]?.GetValue<string>() == "interactive"
                    && httpKnown.SentMessages[1].Body["msg_type"]?.GetValue<string>() == "text",
                    $"HTTP 400 interactive card error must fall back to plain text, got count={httpKnown.SentMessages.Count}");
            }
            finally { await gwKnown.StopAsync(); }
        });
    }

    private static string EncryptFeishu(string plainJson, string encryptKey)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key;
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(plainJson), iv, PaddingMode.PKCS7);
        var combined = new byte[16 + cipher.Length];
        Buffer.BlockCopy(iv, 0, combined, 0, 16);
        Buffer.BlockCopy(cipher, 0, combined, 16, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    private static string BuildEvent(
        string eventId,
        string messageId,
        string messageType,
        string contentJson,
        JsonArray? mentions = null,
        string senderType = "user",
        string? rootId = null,
        string? threadId = null)
    {
        var msg = new JsonObject
        {
            ["message_id"] = messageId,
            ["chat_id"] = "oc_synthetic",
            ["chat_type"] = "group",
            ["message_type"] = messageType,
            ["content"] = contentJson
        };
        if (mentions is not null) msg["mentions"] = mentions;
        if (rootId is not null) msg["root_id"] = rootId;
        if (threadId is not null) msg["thread_id"] = threadId;

        return new JsonObject
        {
            ["token"] = "tok-mention",
            ["header"] = new JsonObject { ["event_id"] = eventId, ["event_type"] = "im.message.receive_v1" },
            ["event"] = new JsonObject
            {
                ["sender"] = new JsonObject
                {
                    ["sender_type"] = senderType,
                    ["sender_id"] = new JsonObject { ["open_id"] = "ou_alice" }
                },
                ["message"] = msg
            }
        }.ToJsonString();
    }

    private sealed class RecoverablePhase2Dedup : IWebhookDedupStore
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public bool Fail { get; set; }
        public bool TryRegister(IReadOnlyList<string> keys, DateTimeOffset seenAt, TimeSpan ttl)
        {
            if (Fail) throw new InvalidOperationException("synthetic_dedup_down");
            lock (_seen)
            {
                if (keys.Any(_seen.Contains)) return false;
                foreach (var k in keys) _seen.Add(k);
                return true;
            }
        }
    }

    private sealed class InMemoryFeishuWsSession : IFeishuWsSession
    {
        private readonly ChannelQueue<byte[]> _serverToClient = new();
        private readonly ChannelQueue<FeishuWsFrame> _clientToServer = new();
        private volatile bool _open = true;
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsOpen => _open;

        public void EnqueueServerFrame(FeishuWsFrame frame)
        {
            Connected.TrySetResult();
            _serverToClient.Enqueue(FeishuWsFrameCodec.Encode(frame));
        }

        public Task<FeishuWsFrame> NextClientFrameAsync(TimeSpan timeout)
            => _clientToServer.DequeueAsync(timeout);

        public Task SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (FeishuWsFrameCodec.TryDecode(payload.Span, out var decoded) && decoded is not null
                && decoded.Method == FeishuWsFrameCodec.MethodData)
            {
                _clientToServer.Enqueue(decoded);
            }
            return Task.CompletedTask;
        }

        public async Task<(WebSocketMessageType MessageType, byte[] Data, bool Closed)> ReceiveMessageAsync(CancellationToken ct)
        {
            Connected.TrySetResult();
            var bytes = await _serverToClient.DequeueAsync(ct).ConfigureAwait(false);
            return _open ? (WebSocketMessageType.Binary, bytes, false) : (WebSocketMessageType.Close, [], true);
        }

        public Task CloseGracefullyAsync(CancellationToken ct)
        {
            _open = false;
            return Task.CompletedTask;
        }

        public void Dispose() => _open = false;
    }

    private sealed class ChannelQueue<T>
    {
        private readonly ConcurrentQueue<T> _q = new();
        private readonly SemaphoreSlim _signal = new(0);
        public void Enqueue(T item) { _q.Enqueue(item); _signal.Release(); }
        public async Task<T> DequeueAsync(CancellationToken ct)
        {
            await _signal.WaitAsync(ct).ConfigureAwait(false);
            _q.TryDequeue(out var item);
            return item!;
        }
        public async Task<T> DequeueAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await DequeueAsync(cts.Token).ConfigureAwait(false);
        }
    }

    private sealed class Phase2FakeHttp : IPlatformHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public string? BotOpenId { get; set; }
        public byte[] ResourceBytes { get; set; } = [1, 2, 3];
        public string QuotedText { get; set; } = "quoted";
        public HttpStatusCode ChatInfoStatus { get; set; } = HttpStatusCode.OK;
        public int FailInteractiveCount { get; set; }
        public int ChatInfoCalls { get; private set; }
        public int ImageUploads { get; private set; }
        public int Recalls { get; private set; }
        public List<(string Path, JsonObject Body)> SentMessages { get; } = [];

        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
            => Task.FromResult(Json(HttpStatusCode.OK, "{\"code\":0,\"tenant_access_token\":\"tok-p2\",\"expire\":7200}"));

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/callback/ws/endpoint"))
            {
                return Json(HttpStatusCode.OK, "{\"code\":0,\"data\":{\"URL\":\"wss://ws.example.com/ws?service_id=77\",\"ClientConfig\":{\"PingInterval\":60,\"ReconnectInterval\":5}}}");
            }
            if (path.EndsWith("/open-apis/bot/v3/info"))
            {
                return BotOpenId is null
                    ? Json(HttpStatusCode.OK, "{\"code\":0}")
                    : Json(HttpStatusCode.OK, new JsonObject { ["code"] = 0, ["bot"] = new JsonObject { ["open_id"] = BotOpenId } }.ToJsonString());
            }
            if (path.Contains("/resources/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ResourceBytes) };
            }
            if (path.Contains("/open-apis/im/v1/chats/"))
            {
                ChatInfoCalls++;
                return ChatInfoStatus == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, "{\"code\":0,\"data\":{\"name\":\"合成项目群\"}}")
                    : Json(ChatInfoStatus, "{\"code\":99991663}");
            }
            if (request.Method == HttpMethod.Get && path.Contains("/open-apis/im/v1/messages/"))
            {
                var body = new JsonObject
                {
                    ["code"] = 0,
                    ["data"] = new JsonObject
                    {
                        ["items"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["msg_type"] = "text",
                                ["sender"] = new JsonObject { ["id"] = "ou_quoted_sender" },
                                ["body"] = new JsonObject { ["content"] = new JsonObject { ["text"] = QuotedText }.ToJsonString() }
                            }
                        }
                    }
                }.ToJsonString();
                return Json(HttpStatusCode.OK, body);
            }
            if (path.EndsWith("/open-apis/im/v1/images"))
            {
                ImageUploads++;
                return Json(HttpStatusCode.OK, "{\"code\":0,\"data\":{\"image_key\":\"img_uploaded_01\"}}");
            }
            if (request.Method == HttpMethod.Delete && path.Contains("/open-apis/im/v1/messages/"))
            {
                Recalls++;
                return Json(HttpStatusCode.OK, "{\"code\":0}");
            }
            if (request.Method == HttpMethod.Post && path.Contains("/open-apis/im/v1/messages"))
            {
                var reqText = await request.Content!.ReadAsStringAsync(ct);
                SentMessages.Add((path, JsonNode.Parse(reqText)!.AsObject()));
                if (FailInteractiveCount > 0 && reqText.Contains("\"interactive\"", StringComparison.Ordinal))
                {
                    FailInteractiveCount--;
                    return Json(HttpStatusCode.BadRequest, "{\"code\":230099,\"msg\":\"card_rejected\"}");
                }
                return Json(HttpStatusCode.OK, $"{{\"code\":0,\"data\":{{\"message_id\":\"om_sent_{SentMessages.Count}\"}}}}");
            }
            return Json(HttpStatusCode.OK, "{\"code\":0}");
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default)
            => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();

        private static HttpResponseMessage Json(HttpStatusCode code, string body)
            => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
