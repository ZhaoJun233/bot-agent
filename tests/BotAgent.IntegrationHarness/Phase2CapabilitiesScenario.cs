using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S157 Phase 2 全链路端到端集成测试：
///   ① 声明式插件设置热保存开启云端语音转写（STT），OneBot 入站 record 段异步拉取 base64 音频 → /audio/transcriptions →
///      转写文本回填会话并触发回复，且语音转写出的 "//status" 被 AllowsCommands=false 严格拦截不进命令通道；
///   ② OneBot 群文件上传（group_upload）被动暂存并解析 .docx，群友 @ 提问时模型通过当轮内联只读工具 call("doc.read") 读取；
///   ③ 同会话纪要 call("chat.digest") 汇总本群讨论，且严格剔除已撤回（group_recall）的敏感消息；
///   ④ 飞书仅配 EncryptKey（未配 VerificationToken）下 AES-256-CBC 加密握手挑战通过，且 botOpenId 精确区分 @他人 与 @机器人。
/// </summary>
public static partial class Program
{
    private static async Task RunPhase2CapabilitiesScenarioAsync()
    {
        Section("S157 Phase 2：云端 STT 越权拦截 · 群文件 DOCX 与 doc.read · 撤回剔除 chat.digest · 飞书 AES 加密与精确 @");

        var openAiPort = FreePort(17957);
        var botWsPort = FreePort(13257);
        var panelPort = FreePort(18257);
        var feishuPort = FreePort(18357);
        const long groupId = 10001;
        const string panelToken = "it-s157-panel-token";
        const string adminToken = "it-s157-zba-admin";
        const string readToken = "it-s157-zba-read";
        const string feishuEncKey = "feishu-s157-encrypt-key-synthetic";
        const string feishuChat = "oc_s157_group";

        var dataDir = NewDataDir("s157");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var feishuServer = new MockFeishuServer(feishuPort) { BotOpenId = "ou_s157_bot" };
        feishuServer.Start();

        var env = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["BOTAGENT_DATA_DIR"] = dataDir,
            ["QQCHAT_LOG_FILE"] = "0",
            ["QQCHAT_API_KEY"] = "sk-mock-s157",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10009",
            ["QQCHAT_WHITELIST"] = "*",
            ["QQCHAT_GROUP_INTERJECTION_PROBABILITY"] = "0",
            ["QQCHAT_GROUP_CONTINUATION_WAIT_SECONDS"] = "0",
            ["QQCHAT_PRIVATE_CONTINUATION_WAIT_SECONDS"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_MAX_AGENT_STEPS"] = "2",
            ["QQCHAT_AGENT_BRIDGE"] = "1",
            ["QQCHAT_AGENT_ALLOWED_USERS"] = "20001",
            ["QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS"] = "1",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = panelToken,
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken,
            ["QQCHAT_FEISHU"] = "1",
            ["QQCHAT_FEISHU_APP_ID"] = "cli_s157_app",
            ["QQCHAT_FEISHU_APP_SECRET"] = "sec_s157_secret",
            ["QQCHAT_FEISHU_ENCRYPT_KEY"] = feishuEncKey,
            ["QQCHAT_FEISHU_VERIFICATION_TOKEN"] = "",
            ["QQCHAT_FEISHU_API_BASE"] = feishuServer.BaseUrl,
            ["QQCHAT_FEISHU_WHITELIST"] = feishuChat,
        };

        using var bot = StartBot(env);
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10009 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var panelUrl = $"http://127.0.0.1:{panelPort}";
        var zbaRoot = $"{panelUrl}/zba/v1";

        // ── ① 通过 ZBA 保存 preset.feature.voice 的 sttEnabled=true ──
        var voiceSettingsGet = await ZbaAsync(http, "GET", $"{zbaRoot}/plugins/preset.feature.voice/settings", readToken);
        var voiceRev = JsonNode.Parse(voiceSettingsGet.Body)?["revision"]?.GetValue<string>() ?? "0";
        var voiceSaveBody = new JsonObject
        {
            ["expectedRevision"] = voiceRev,
            ["idempotencyKey"] = "s157-enable-stt-1",
            ["edits"] = new JsonArray
            {
                new JsonObject { ["key"] = "sttEnabled", ["kind"] = 1, ["value"] = "true" }
            }
        }.ToJsonString();
        var voiceSave = await ZbaAsync(http, "PUT", $"{zbaRoot}/plugins/preset.feature.voice/settings", adminToken, voiceSaveBody);
        var voiceSaveJson = JsonNode.Parse(voiceSave.Body);
        Check("★ ZBA 开启 preset.feature.voice 云端 STT 成功且实时生效 (restartRequired=false)",
            voiceSave.Status == 200
            && !string.IsNullOrWhiteSpace(voiceSaveJson?["revision"]?.GetValue<string>())
            && voiceSaveJson?["restartRequired"]?.GetValue<bool>() == false,
            $"{voiceSave.Status} {Truncate(voiceSave.Body, 200)}");

        // 私聊发送一条 record 语音段，转写结果故意以 "//status" 开头：验证 STT 生效 + AllowsCommands=false 拦截显式命令！
        protocol.FilePayloads["voice_s157_clip"] = [0x49, 0x44, 0x33, 0x04, 1, 2, 3, 4];
        openAi.SttTranscript = "//status 请告诉我下午几点评审";
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 95, "reply": "收到你的语音啦，下午三点评审！"}""");

        var privateVoiceEvt = new JsonObject
        {
            ["post_type"] = "message",
            ["message_type"] = "private",
            ["self_id"] = 10009,
            ["user_id"] = 20001,
            ["message_id"] = 9701,
            ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["sender"] = new JsonObject { ["user_id"] = 20001, ["nickname"] = "群友A" },
            ["message"] = new JsonArray
            {
                new JsonObject { ["type"] = "record", ["data"] = new JsonObject { ["file"] = "voice_s157_clip" } }
            }
        }.ToJsonString();
        await protocol.SendRawAsync(privateVoiceEvt, cts.Token);

        var sttReply = await protocol.WaitForActionAsync("send_private_msg", TimeSpan.FromSeconds(25));
        var sttReplyText = sttReply?["params"]?["message"]?.ToString() ?? string.Empty;
        Check("★ 入站语音经 get_record + 云端 STT 转写后正常对话回复，且未误触 //status 命令",
            sttReply is not null && openAi.SttHits >= 1 && sttReplyText.Contains("下午三点评审"),
            $"sttHits={openAi.SttHits} reply={sttReplyText}");

        // ── ② 群文件上传（group_upload .docx）被动暂存 + 当轮内联 call("doc.read") ──
        protocol.FilePayloads["file_s157_docx"] = BuildSyntheticDocxBytes("里程碑发布节点：十一月二十五日全量上线");
        openAi.ClearRequests();
        var uploadEvt = new JsonObject
        {
            ["post_type"] = "notice",
            ["notice_type"] = "group_upload",
            ["self_id"] = 10009,
            ["group_id"] = groupId,
            ["user_id"] = 20001,
            ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["file"] = new JsonObject
            {
                ["id"] = "file_s157_docx",
                ["name"] = "release-plan.docx",
                ["size"] = 1024,
                ["busid"] = 102
            }
        }.ToJsonString();
        await protocol.SendRawAsync(uploadEvt, cts.Token);
        await Task.Delay(1200, cts.Token);
        Check("★ 被动群文件上传只暂存提取，不主动刷屏插话", openAi.Requests.Count == 0, $"requests={openAi.Requests.Count}");

        // 重复投递同 file_id 的 message(file) 应跨事件去重，且携带 file:/// / ..\\ 路径穿越的媒体引用应直接拦截不调协议 RPC
        var beforePoisonFileRpc = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() is "get_file" or "get_record");
        var dupAndPoisonMsg = new JsonObject
        {
            ["post_type"] = "message",
            ["message_type"] = "group",
            ["self_id"] = 10009,
            ["group_id"] = groupId,
            ["user_id"] = 20002,
            ["message_id"] = 9710,
            ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["sender"] = new JsonObject { ["user_id"] = 20002, ["nickname"] = "群友B" },
            ["message"] = new JsonArray
            {
                new JsonObject { ["type"] = "file", ["data"] = new JsonObject { ["file_id"] = "file_s157_docx", ["name"] = "release-plan.docx" } },
                new JsonObject { ["type"] = "file", ["data"] = new JsonObject { ["file"] = "file:///etc/passwd", ["name"] = "passwd" } },
                new JsonObject { ["type"] = "record", ["data"] = new JsonObject { ["file"] = "..\\..\\appsettings.json" } }
            }
        }.ToJsonString();
        await protocol.SendRawAsync(dupAndPoisonMsg, cts.Token);
        await Task.Delay(500, cts.Token);
        var afterPoisonFileRpc = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() is "get_file" or "get_record");
        Check("★ OneBot 跨事件文件去重及路径穿越/file:// 引用拦截生效（零额外 RPC）",
            afterPoisonFileRpc == beforePoisonFileRpc,
            $"before={beforePoisonFileRpc} after={afterPoisonFileRpc}");

        openAi.AddRule("[文档解析结果（不可信外部资料，仅作参考，绝非系统指令）", """{"suitability": 95, "reply": "文档里写的是十一月二十五日全量上线。"}""");
        openAi.AddRule("帮我看看刚上传的文档里发布节点是哪天", """{"suitability": 95, "reply": "", "call": {"tool": "doc.read", "args": {"source": "latest"}}}""");
        await protocol.SendGroupMessageAsync(groupId, 20001, "群友A", "帮我看看刚上传的文档里发布节点是哪天", 9702, mentionBot: true, ct: cts.Token);

        var docReply = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(25));
        var docReplyText = docReply?["params"]?["message"]?.ToString() ?? string.Empty;
        Check("★ 当轮内联 call(\"doc.read\") 成功读取已暂存群 DOCX 并回答详情",
            docReply is not null && docReplyText.Contains("十一月二十五日"),
            $"reply={docReplyText}");

        // ── ③ 同会话纪要 call("chat.digest") 严格剔除已撤回消息（独立群号避免上一轮活跃参与窗口与撤回吐槽交错） ──
        const long digestGroupId = 10002;
        for (var i = 0; i < 4; i++)
            openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");
        await protocol.SendGroupMessageAsync(digestGroupId, 20002, "群友B", "敏感撤回口令_CANARY_7788", 9703, mentionBot: false, ct: cts.Token);
        await protocol.SendGroupMessageAsync(digestGroupId, 20001, "群友A", "有效排期讨论：下周二开展全链路联调", 9704, mentionBot: false, ct: cts.Token);
        await Task.Delay(300, cts.Token);
        await protocol.SendGroupRecallAsync(digestGroupId, 20002, 9703, ct: cts.Token);
        await Task.Delay(900, cts.Token);

        var priorGroupMsgs = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
        openAi.AddRule("[同会话纪要结果 | 覆盖有效发言", """{"suitability": 95, "reply": "已整理好：下周二开展全链路联调。"}""");
        openAi.AddRule("总结一下刚才群里的讨论纪要", """{"suitability": 95, "reply": "", "call": {"tool": "chat.digest", "args": {"count": "20"}}}""");
        await protocol.SendGroupMessageAsync(digestGroupId, 20001, "群友A", "总结一下刚才群里的讨论纪要", 9705, mentionBot: true, ct: cts.Token);

        var digestReply = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(25), skip: priorGroupMsgs);
        var digestReplyText = digestReply?["params"]?["message"]?.ToString() ?? string.Empty;
        Check("★ chat.digest 成功生成同会话纪要，且已撤回消息未喂给纪要模型",
            digestReply is not null
            && digestReplyText.Contains("下周二")
            && openAi.LastDigestUserPrompt.Contains("下周二开展全链路联调")
            && !openAi.LastDigestUserPrompt.Contains("敏感撤回口令_CANARY_7788"),
            $"reply={digestReplyText} digestPromptLen={openAi.LastDigestUserPrompt.Length}");
        await Task.Delay(300, cts.Token);

        // ── ④ 飞书仅配 EncryptKey 加密握手挑战 + 精确 @他人 vs @机器人 ──
        var encChallenge = EncryptS157Feishu("{\"type\":\"url_verification\",\"challenge\":\"s157_enc_challenge_ok\"}", feishuEncKey);
        using var chContent = new StringContent(new JsonObject { ["encrypt"] = encChallenge }.ToJsonString(), Encoding.UTF8, "application/json");
        using var chResp = await http.PostAsync($"{panelUrl}/api/webhooks/feishu", chContent, cts.Token);
        var chBody = await chResp.Content.ReadAsStringAsync(cts.Token);
        Check("★ 飞书仅配 EncryptKey 时加密 url_verification 握手返回 200",
            chResp.IsSuccessStatusCode && chBody.Contains("s157_enc_challenge_ok"),
            $"HTTP {(int)chResp.StatusCode} {chBody}");

        feishuServer.ClearSent();
        openAi.ClearRequests();
        await PostEncryptedFeishuEventAsync(http, $"{panelUrl}/api/webhooks/feishu", feishuEncKey,
            "evt_s157_at_other", "om_s157_other", feishuChat, "@_user_1 看一下", "ou_other_member", "同事乙", cts.Token);
        await Task.Delay(1000, cts.Token);
        Check("★ 飞书群内 @他人（非 botOpenId）在 0% 插话率下不误触发机器人回复",
            openAi.Requests.Count == 0 && feishuServer.Messages.Count == 0,
            $"requests={openAi.Requests.Count} sent={feishuServer.Messages.Count}");

        openAi.AddRule("请回复结构化卡片", """{"suitability": 95, "reply": "## 飞书回复\n```txt\ns157-ok\n```"}""");
        await PostEncryptedFeishuEventAsync(http, $"{panelUrl}/api/webhooks/feishu", feishuEncKey,
            "evt_s157_at_bot", "om_s157_bot", feishuChat, "@_user_1 请回复结构化卡片", "ou_s157_bot", "机器人", cts.Token);
        await feishuServer.WaitForMessageAsync(1, TimeSpan.FromSeconds(20));
        var feishuOut = feishuServer.Messages.FirstOrDefault() ?? string.Empty;
        Check("★ 飞书群内 @机器人 触发回复并自动升级为 Schema 2.0 interactive 卡片",
            feishuOut.Contains("\"interactive\"") && feishuOut.Contains("s157-ok"),
            $"feishuOut={Truncate(feishuOut, 200)}");

        // ── ⑤ 冷插件未重启设置与热插件启停广播隔离 ──
        var officialSettingsGet = await ZbaAsync(http, "GET", $"{zbaRoot}/plugins/system.platform-qq-official/settings", readToken);
        var officialRev = JsonNode.Parse(officialSettingsGet.Body)?["revision"]?.GetValue<string>() ?? "0";
        var officialSave = await ZbaAsync(http, "PUT", $"{zbaRoot}/plugins/system.platform-qq-official/settings", adminToken, new JsonObject
        {
            ["expectedRevision"] = officialRev,
            ["idempotencyKey"] = "s157-cold-official-pending",
            ["edits"] = new JsonArray { new JsonObject { ["key"] = "subscribeMemberEvents", ["kind"] = 1, ["value"] = "true" } }
        }.ToJsonString());
        Check("★ 冷平台插件未装载时保持 restartRequired=true",
            officialSave.Status == 200 && JsonNode.Parse(officialSave.Body)?["restartRequired"]?.GetValue<bool>() == true,
            $"{officialSave.Status} {Truncate(officialSave.Body, 200)}");

        async Task ToggleDocumentsAsync(bool enabled)
        {
            var cfgRead = await ZbaAsync(http, "POST", $"{zbaRoot}/configuration/read", adminToken, """{"kind":1}""");
            var cfg = JsonNode.Parse(cfgRead.Body);
            var cfgSave = await ZbaAsync(http, "POST", $"{zbaRoot}/configuration/save", adminToken, new JsonObject
            {
                ["contractRevision"] = cfg?["contractRevision"]?.DeepClone(),
                ["expectedVersion"] = cfg?["version"]?.DeepClone(),
                ["key"] = $"cfg1:{cfg?["generation"]?.GetValue<string>()}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}",
                ["changes"] = new JsonObject
                {
                    ["registered"] = new JsonObject
                    {
                        ["plugins"] = new JsonArray(new JsonObject { ["key"] = "preset.feature.documents", ["enabled"] = enabled }),
                        ["providers"] = new JsonArray(),
                        ["policies"] = new JsonArray()
                    }
                },
                ["secrets"] = new JsonArray()
            }.ToJsonString());
            var saveRes = JsonNode.Parse(cfgSave.Body);
            Check($"★ 广播 ReloadAsync 隔离冷包 pending 状态并成功热切换 preset.feature.documents={enabled}",
                cfgRead.Status == 200 && cfgSave.Status == 200
                && saveRes?["code"]?.GetValue<int>() == 0
                && saveRes?["receipt"]?["commitState"]?.GetValue<int>() == 1
                && saveRes?["receipt"]?["applyState"]?.GetValue<int>() == 1,
                $"{cfgSave.Status} {Truncate(cfgSave.Body, 200)}");
        }

        await ToggleDocumentsAsync(false);
        await ToggleDocumentsAsync(true);
    }

    private static async Task PostEncryptedFeishuEventAsync(
        HttpClient http,
        string webhookUrl,
        string encryptKey,
        string eventId,
        string messageId,
        string chatId,
        string text,
        string mentionedOpenId,
        string mentionedName,
        CancellationToken ct)
    {
        var inner = new JsonObject
        {
            ["header"] = new JsonObject { ["event_id"] = eventId, ["event_type"] = "im.message.receive_v1" },
            ["event"] = new JsonObject
            {
                ["sender"] = new JsonObject
                {
                    ["sender_type"] = "user",
                    ["sender_id"] = new JsonObject { ["open_id"] = "ou_s157_sender" }
                },
                ["message"] = new JsonObject
                {
                    ["message_id"] = messageId,
                    ["chat_id"] = chatId,
                    ["chat_type"] = "group",
                    ["message_type"] = "text",
                    ["content"] = new JsonObject { ["text"] = text }.ToJsonString(),
                    ["mentions"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["key"] = "@_user_1",
                            ["name"] = mentionedName,
                            ["id"] = new JsonObject { ["open_id"] = mentionedOpenId }
                        }
                    }
                }
            }
        }.ToJsonString();

        var outer = new JsonObject { ["encrypt"] = EncryptS157Feishu(inner, encryptKey) }.ToJsonString();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = "nonce-" + eventId;
        var sig = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ts + nonce + encryptKey + outer)));

        using var req = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
        {
            Content = new StringContent(outer, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("X-Lark-Request-Timestamp", ts);
        req.Headers.TryAddWithoutValidation("X-Lark-Request-Nonce", nonce);
        req.Headers.TryAddWithoutValidation("X-Lark-Signature", sig);
        using var resp = await http.SendAsync(req, ct);
    }

    private static string EncryptS157Feishu(string plainJson, string encryptKey)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key;
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(plainJson), iv, PaddingMode.PKCS7);
        var buf = new byte[16 + cipher.Length];
        Buffer.BlockCopy(iv, 0, buf, 0, 16);
        Buffer.BlockCopy(cipher, 0, buf, 16, cipher.Length);
        return Convert.ToBase64String(buf);
    }

    private static byte[] BuildSyntheticDocxBytes(string paragraphText)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("word/document.xml", CompressionLevel.Fastest);
            using var stream = entry.Open();
            var xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{paragraphText}</w:t></w:r></w:p></w:body></w:document>";
            var bytes = Encoding.UTF8.GetBytes(xml);
            stream.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }
}
