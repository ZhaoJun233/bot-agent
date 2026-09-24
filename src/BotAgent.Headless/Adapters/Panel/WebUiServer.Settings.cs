using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Settings;
using BotAgent.Adapters.Persistence;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    /// <summary>
    /// 把云端 TTS 的密钥写给 tts 容器（它是**另一个进程**，读不到我们的 SQLite 密钥库）。
    ///
    /// 路径：机器人的 <c>/host/qqchat</c> 就是宿主机部署目录（compose 里常挂），
    /// 所以写 <c>/host/qqchat/tts-conf/tts.env</c>；tts 容器把同一目录挂在 <c>/conf</c>，
    /// 每次请求读一次（带 mtime 缓存）——于是**改完面板不用重启任何容器**。
    /// 本地开发（没有 /host/qqchat）就落在数据目录，拿不到就只记日志（不报错）。
    /// </summary>
    private void WriteTtsConfToHost()
    {
        try
        {
            var key = _secrets.LoadTtsKey() ?? string.Empty;
            var body =
                "# 由机器人面板写入（不要手改：改了会被面板覆盖）\n" +
                $"MINIMAX_API_KEY={key}\n" +
                $"OPENAI_TTS_API_KEY={key}\n" +
                $"TTS_PROVIDER={_settings.TtsProvider}\n" +
                // 面板只给一个“云端接口地址”框：按选中的服务商写对应的那个变量，避免两个都写导致歧义。
                // 空值不算覆盖：代理侧是 `面板值 or 容器环境变量`，所以留空就回到容器默认。
                (Domain.Qq.Channels.IsOfficial(string.Empty) ? string.Empty : string.Empty) +
                (string.Equals(_settings.TtsProvider, "openai", StringComparison.OrdinalIgnoreCase)
                    ? $"OPENAI_TTS_BASE_URL={_settings.TtsApiBase}\nOPENAI_TTS_MODEL={_settings.TtsModel}\n"
                    : $"MINIMAX_API_BASE={_settings.TtsApiBase}\nMINIMAX_MODEL={_settings.TtsModel}\n") +
                $"# 写入时间：{Clock.Now:yyyy-MM-dd HH:mm:ss}\n";

            if (TtsConfFile.TryWrite(body))
            {
                return;
            }

            FileLog.Write("Web", "TTS 密钥已存库，但没写成配置文件（宿主机目录不可写）——语音可能仍用环境变量里的 key");
        }
        catch (Exception ex)
        {
            FileLog.Write("Web", "写 TTS 配置失败（不影响机器人本身）：" + ex.Message);
        }
    }

        /// <summary>
    /// 面板回显用的参与上限摘要（P1）。**走 Clamped()**：面板上看到的永远是“真正生效的那组数”，
    /// 不是你在输入框里填的 —— 填 999 时这里会显示被钳到的 10，免得“我改了怎么没变化”这类误会。
    /// </summary>
    private static string DescribeParticipationPolicy(AppSettings s)
    {
        var p = new Services.Participation.ParticipationPolicy(
            s.ParticipationMaxConsecutiveReplies,
            s.ParticipationCooldownSeconds,
            s.ParticipationProbingMaxReplies,
            s.ParticipationMaxActiveLifetimeSeconds,
            s.ParticipationMaxExitingLifetimeSeconds).Clamped();

        return $"连续 ≤{p.MaxConsecutiveReplies} / 冷却 {p.CooldownSeconds}s / 试探 ≤{p.ProbingMaxReplies}"
               + $" / 活性命 {p.MaxActiveLifetimeSeconds}s / 退场 {p.MaxExitingLifetimeSeconds}s";
    }

/// <summary>密钥掩码（只回显前 3 后 2，与其它密钥一致）。</summary>
    private static string MaskSecret(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return text.Length <= 8 ? "***" : text[..3] + "***" + text[^2..];
    }

    private async Task HandleSettingsSaveAsync(HttpListenerContext context)
    {
        var body = await ReadJsonAsync(context);
        if (body is null)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "invalid json" });
            return;
        }

        // 模型端点：先校验再应用 —— 无效 URL 直接 400，不然一次手滑就把配置写坏、连不上模型。
        var newBaseUrl = body["modelBaseUrl"] is JsonNode mbu ? (mbu.GetValue<string>() ?? string.Empty).Trim() : null;
        if (newBaseUrl is { Length: > 0 } &&
            (!Uri.TryCreate(newBaseUrl, UriKind.Absolute, out var parsedBaseUrl) ||
             (parsedBaseUrl.Scheme != Uri.UriSchemeHttp && parsedBaseUrl.Scheme != Uri.UriSchemeHttps)))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "Base URL 要形如 http://host:port/v1（http/https 开头的完整地址）" });
            return;
        }

        // 只接受运行时可改的字段（协议端地址、QQ 号这些仍属于容器环境变量职责）
        _settingsHotReload.ApplyRuntimeSettings(s =>
        {
            // 三段分开：行为/阈值 · 通道与 agent · 报表与模型。顺序与措辞一字未改（§5.3 兼容红线）。
            ApplyBehaviorSettings(body, s);
            ApplyChannelAndAgentSettings(body, s);
            ApplyReportingAndModelSettings(body, s, newBaseUrl);
        });

        // 定时类功能：开关/时刻/收件人变了一定要重排定时器，否则“改了不生效”（要重启才变）
        _healthReports?.Reapply();

        await WriteJsonAsync(context, 200, BuildSettingsPayload());
    }

    /// <summary>行为与阈值：人设 / 三份白名单 / 欲望与阈值 / 各种冷却 / 上下文窗口 / 画像 / 表情包。</summary>
    private void ApplyBehaviorSettings(JsonNode body, AppSettings s)
    {
            if (body["botPersona"] is JsonNode persona) s.BotPersona = persona.GetValue<string>() ?? string.Empty;
            if (body["messageWhitelist"] is JsonNode wl) s.MessageWhitelist = wl.GetValue<string>() ?? string.Empty;
        if (body["whitelistGroups"] is JsonNode wlg) s.WhitelistGroups = wlg.GetValue<string>() ?? string.Empty;
        if (body["whitelistPrivates"] is JsonNode wlp) s.WhitelistPrivates = wlp.GetValue<string>() ?? string.Empty;
            if (body["aiDesire"] is JsonNode desire) s.AiDesire = Math.Clamp(desire.GetValue<int>(), 0, 100);
            if (body["suitabilityThreshold"] is JsonNode th) s.SuitabilityThreshold = Math.Clamp(th.GetValue<int>(), 0, 100);
            if (body["aiModeEnabled"] is JsonNode ai) s.AiModeEnabled = ai.GetValue<bool>();
            if (body["maxTokens"] is JsonNode mt) s.MaxTokens = Math.Clamp(mt.GetValue<int>(), 64, 32000);
            if (body["groupCooldownSeconds"] is JsonNode gc) s.GroupCooldownSeconds = Math.Max(0, gc.GetValue<int>());
            if (body["privateCooldownSeconds"] is JsonNode pc) s.PrivateCooldownSeconds = Math.Max(0, pc.GetValue<int>());
            if (body["idleFallbackSeconds"] is JsonNode fb) s.IdleFallbackSeconds = Math.Max(0, fb.GetValue<int>());
            if (body["splitReplies"] is JsonNode sp) s.SplitReplies = sp.GetValue<bool>();
            if (body["enableProactive"] is JsonNode pv) s.EnableProactive = pv.GetValue<bool>();
            if (body["proactiveCooldownSeconds"] is JsonNode pcd) s.ProactiveCooldownSeconds = Math.Clamp(pcd.GetValue<int>(), 60, 86400);
            if (body["proactiveQuietSeconds"] is JsonNode pq) s.ProactiveQuietSeconds = Math.Clamp(pq.GetValue<int>(), 1, 3600);
            if (body["ignoreBracketMessages"] is JsonNode ibm) s.IgnoreBracketMessages = ibm.GetValue<bool>();
            if (body["segmentDelayMs"] is JsonNode sd) s.SegmentDelayMs = Math.Max(0, sd.GetValue<int>());
            if (body["maxContextMessages"] is JsonNode mc) s.MaxContextMessages = Math.Clamp(mc.GetValue<int>(), 10, 1000);
            if (body["profileLookupCount"] is JsonNode pl) s.ProfileLookupCount = Math.Clamp(pl.GetValue<int>(), 0, 50);
            if (body["profileSummaryLines"] is JsonNode psl) s.ProfileSummaryLines = Math.Clamp(psl.GetValue<int>(), 0, 50);
            if (body["maxProfileChars"] is JsonNode mpc) s.MaxProfileChars = Math.Clamp(mpc.GetValue<int>(), 0, 20000);
            if (body["maxMessagesPerConversation"] is JsonNode mm) s.MaxMessagesPerConversation = Math.Clamp(mm.GetValue<int>(), 20, 100000);
            if (body["maxConcurrentReplies"] is JsonNode mcr) s.MaxConcurrentReplies = Math.Clamp(mcr.GetValue<int>(), 1, 16);
            if (body["enableProfileSummary"] is JsonNode eps) s.EnableProfileSummary = eps.GetValue<bool>();
            if (body["profileSummaryThreshold"] is JsonNode pst) s.ProfileSummaryThreshold = Math.Clamp(pst.GetValue<int>(), 5, 500);
            if (body["profileSummaryMaxChars"] is JsonNode psm) s.ProfileSummaryMaxChars = Math.Clamp(psm.GetValue<int>(), 40, 2000);
            if (body["profileSummaryIntervalSeconds"] is JsonNode psi) s.ProfileSummaryIntervalSeconds = Math.Clamp(psi.GetValue<int>(), 0, 86400);

            // ---- 表情包 ----
            if (body["enableVoice"] is JsonNode ev) s.EnableVoice = ev.GetValue<bool>();
        if (body["voiceName"] is JsonNode vn) s.VoiceName = vn.GetValue<string>().Trim();
        if (body["voiceSpeed"] is JsonNode vs) s.VoiceSpeed = Math.Clamp(vs.GetValue<int>(), 50, 200);
            if (body["voicePitch"] is JsonNode vp) s.VoicePitch = Math.Clamp(vp.GetValue<int>(), -12, 12);
            if (body["voiceVol"] is JsonNode vv) s.VoiceVol = Math.Clamp(vv.GetValue<int>(), 0, 1000);
            if (body["voiceEmotion"] is JsonNode ve) s.VoiceEmotion = (ve.GetValue<string>() ?? string.Empty).Trim();
        if (body["voiceMaxChars"] is JsonNode vmc) s.VoiceMaxChars = Math.Clamp(vmc.GetValue<int>(), 10, 300);
        if (body["voiceEagerness"] is JsonNode vge) s.VoiceEagerness = Math.Clamp(vge.GetValue<int>(), 0, 100);
        if (body["ttsServiceUrl"] is JsonNode tts) s.TtsServiceUrl = tts.GetValue<string>().Trim();

        // 云端服务商/地址/模型：都不是密钥，跟着行为配置进 settings.json；
        // 但同样要**写给 tts 容器**（那边才是真正调用云端的一方）。
        var ttsConfDirty = false;
        if (body["ttsProvider"] is JsonNode ttp)
        {
            s.TtsProvider = ttp.GetValue<string>().Trim();
            ttsConfDirty = true;
        }

        if (body["ttsApiBase"] is JsonNode tab)
        {
            s.TtsApiBase = tab.GetValue<string>().Trim();
            ttsConfDirty = true;
        }

        if (body["ttsModel"] is JsonNode tmd)
        {
            s.TtsModel = tmd.GetValue<string>().Trim();
            ttsConfDirty = true;
        }

        // 云端 TTS 的厂商密钥（面板可改；存密钥库，只回显掩码）——
        // ⚠ **空 = 不改**：面板每次保存都会把这个字段（可能是空的）一起发上来，
        // 早期版本把空当“清空”，结果“改个白名单就把 key 抹了”→ 语音全失败（retcode 1200）。
        // 要清空得显式说 clearTtsApiKey=true。
        if (body["clearTtsApiKey"] is JsonValue clearNode && clearNode.TryGetValue<bool>(out var clear) && clear)
        {
            _secrets.SaveTtsKey(null);
            WriteTtsConfToHost();
            FileLog.Write("Web", "面板显式清空了 TTS 密钥（语音会发不出去，直到重新填）");
        }
        else if (body["ttsApiKey"] is JsonValue ttsKeyValue && ttsKeyValue.TryGetValue<string>(out var rawTtsKey)
                 && !string.IsNullOrWhiteSpace(rawTtsKey))
        {
            _secrets.SaveTtsKey(rawTtsKey.Trim());
            WriteTtsConfToHost();
            FileLog.Write("Web", "面板更新了 TTS 密钥（已掩码保存，并写给 tts 容器）");
        }

        if (ttsConfDirty)
        {
            WriteTtsConfToHost();
            FileLog.Write("Web", $"面板更新了云端 TTS 配置（服务商={s.TtsProvider}，地址={(s.TtsApiBase.Length == 0 ? "(容器默认)" : s.TtsApiBase)}，模型={(s.TtsModel.Length == 0 ? "(容器默认)" : s.TtsModel)}）");
        }

    }

    /// <summary>通道与 agent：官方开放平台 / 本机 `//` 命令 / 路由与服务器内置 agent / 参与与审批 / 服务器 agent 密钥。</summary>
    private void ApplyChannelAndAgentSettings(JsonNode body, AppSettings s)
    {
        // ---- 官方通道（QQ 开放平台）----
        // 只收行为/标识类字段：secret 是密钥，按项目约定**只从环境变量读**（与 ApiKey 一致），
        // 面板不回显也不接收，免得它落进 settings.json 又被备份/贴日志。
        if (body["officialEnabled"] is JsonNode ofe) s.OfficialEnabled = ofe.GetValue<bool>();
        if (body["officialAppId"] is JsonNode oai) s.OfficialAppId = oai.GetValue<string>().Trim();
        if (body["officialSandbox"] is JsonNode osb) s.OfficialSandbox = osb.GetValue<bool>();
        if (body["privateChatEnabled"] is JsonNode pce) s.PrivateChatEnabled = pce.GetValue<bool>();
        if (body["officialChatEnabled"] is JsonNode oce) s.OfficialChatEnabled = oce.GetValue<bool>();

        // AppSecret：与 TTS key 同一套口径 —— **空 = 不改**（面板每次保存都会把这个字段发上来，
        // 把空当“清空”就会“改个白名单把 secret 抹了”）；要清空得显式传 clearOfficialAppSecret。
        if (body["clearOfficialAppSecret"] is JsonValue clearOs && clearOs.TryGetValue<bool>(out var clearOk) && clearOk)
        {
            _secrets.SaveOfficialSecret(null);
            s.OfficialAppSecret = (Environment.GetEnvironmentVariable("QQCHAT_OFFICIAL_APP_SECRET") ?? string.Empty).Trim();
            FileLog.Write("Web", "面板清空了官方通道 AppSecret（回退环境变量）");
        }
        else if (body["officialAppSecret"] is JsonValue osv && osv.TryGetValue<string>(out var rawSecret)
                 && !string.IsNullOrWhiteSpace(rawSecret))
        {
            var newSecret = rawSecret.Trim();
            _secrets.SaveOfficialSecret(newSecret);
            s.OfficialAppSecret = newSecret;
            FileLog.Write("Web", "面板更新了官方通道 AppSecret（已掩码保存；重启后生效）");
        }
        if (body["officialWhitelistGroups"] is JsonNode owg) s.OfficialWhitelistGroups = owg.GetValue<string>().Trim();
        if (body["officialWhitelistPrivates"] is JsonNode owp) s.OfficialWhitelistPrivates = owp.GetValue<string>().Trim();
        if (body["enableWebSearch"] is JsonNode ws) s.EnableWebSearch = ws.GetValue<bool>();
        if (body["webSearchUseModelSearch"] is JsonNode wsm) s.WebSearchUseModelSearch = wsm.GetValue<bool>();
        if (body["webSearchSources"] is JsonNode wss) s.WebSearchSources = wss.GetValue<string>().Trim();
        if (body["webSearchMaxResults"] is JsonNode wsr) s.WebSearchMaxResults = Math.Clamp(wsr.GetValue<int>(), 1, 10);
        if (body["webSearchCooldownSeconds"] is JsonNode wsc) s.WebSearchCooldownSeconds = Math.Clamp(wsc.GetValue<int>(), 0, 86400);
        if (body["webSearchTimeoutSeconds"] is JsonNode wst) s.WebSearchTimeoutSeconds = Math.Clamp(wst.GetValue<int>(), 5, 60);
        if (body["enableLinkPreview"] is JsonNode elp) s.EnableLinkPreview = elp.GetValue<bool>();
        if (body["linkPreviewTimeoutSeconds"] is JsonNode lpt) s.LinkPreviewTimeoutSeconds = Math.Clamp(lpt.GetValue<int>(), 2, 30);
        if (body["linkPreviewMax"] is JsonNode lpm) s.LinkPreviewMax = Math.Clamp(lpm.GetValue<int>(), 0, 5);
        if (body["enableMusic"] is JsonNode em) s.EnableMusic = em.GetValue<bool>();
        if (body["neteaseBaseUrl"] is JsonNode nbu) s.NeteaseBaseUrl = nbu.GetValue<string>().Trim();
        if (body["musicSources"] is JsonNode ms) s.MusicSources = ms.GetValue<string>();
        if (body["musicBitrate"] is JsonNode mb) s.MusicBitrate = Math.Clamp(mb.GetValue<int>(), 32, 320);
        if (body["musicMaxDownloadMb"] is JsonNode mmd) s.MusicMaxDownloadMb = Math.Clamp(mmd.GetValue<int>(), 1, 64);
        if (body["musicMaxAnalysisSeconds"] is JsonNode mma) s.MusicMaxAnalysisSeconds = Math.Clamp(mma.GetValue<int>(), 20, 600);
        if (body["musicLibraryMax"] is JsonNode mml) s.MusicLibraryMax = Math.Clamp(mml.GetValue<int>(), 10, 5000);
        if (body["musicNoteTtlDays"] is JsonNode mnt) s.MusicNoteTtlDays = Math.Clamp(mnt.GetValue<int>(), 1, 365);
        if (body["musicListenCooldownSeconds"] is JsonNode mlc) s.MusicListenCooldownSeconds = Math.Clamp(mlc.GetValue<int>(), 0, 86400);
        if (body["musicUnderstandModel"] is JsonNode mum) s.MusicUnderstandModel = mum.GetValue<string>().Trim();
        if (body["musicSendAudioToModel"] is JsonNode msa) s.MusicSendAudioToModel = msa.GetValue<bool>();
        if (body["musicKeepAudio"] is JsonNode mka) s.MusicKeepAudio = mka.GetValue<bool>();

        // ---- 本机 Agent（// 命令）----
        if (body["enableAgentBridge"] is JsonNode eab) s.EnableAgentBridge = eab.GetValue<bool>();
        if (body["agentPrefix"] is JsonNode apx) s.AgentPrefix = (apx.GetValue<string>() ?? "//").Trim();
        if (body["agentAllowedUsers"] is JsonNode aau) s.AgentAllowedUsers = aau.GetValue<string>() ?? string.Empty;
        if (body["agentWorkDir"] is JsonNode awd) s.AgentWorkDir = (awd.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentModel"] is JsonNode amd) s.AgentModel = (amd.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentTools"] is JsonNode atl) s.AgentTools = (atl.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentTimeoutSeconds"] is JsonNode ats) s.AgentTimeoutSeconds = Math.Clamp(ats.GetValue<int>(), 30, 7200);
        if (body["agentReplyMaxChars"] is JsonNode arc) s.AgentReplyMaxChars = Math.Clamp(arc.GetValue<int>(), 200, 3000);
        if (body["agentProgressSeconds"] is JsonNode aps) s.AgentProgressSeconds = Math.Clamp(aps.GetValue<int>(), 0, 3600);
        if (body["agentMaxQueued"] is JsonNode amq) s.AgentMaxQueued = Math.Clamp(amq.GetValue<int>(), 1, 20);

        // ---- agent 路由与服务器内置 agent ----
        if (body["agentTarget"] is JsonNode atg) s.AgentTarget = (atg.GetValue<string>() ?? "auto").Trim();
        if (body["enableServerAgent"] is JsonNode esa) s.EnableServerAgent = esa.GetValue<bool>();
        if (body["enableHostAgent"] is JsonNode eha) s.EnableHostAgent = eha.GetValue<bool>();
        if (body["agentServerTools"] is JsonNode ast) s.AgentServerTools = (ast.GetValue<string>() ?? string.Empty).Trim();

        // QQ 动作：只认已存在的动作名（别名也翻成规范名），写错的直接忽略 ——
        // 下次读回设置时面板上看到的就是“真正生效的那几个”，不会拿一个拼错的名字骗自己。
        if (body["agentServerQqActions"] is JsonNode asqa)
        {
            var raw = (asqa.GetValue<string>() ?? string.Empty).Trim();
            if (raw.Length == 0 || raw is "all" or "*" or "全部" or "所有")
            {
                s.AgentServerQqActions = raw;
            }
            else
            {
                var names = new List<string>();
                foreach (var piece in raw.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var canonical = QqActionCatalog.Canonical(piece.Trim());
                    if (canonical is not null && !names.Contains(canonical))
                    {
                        names.Add(canonical);
                    }
                }

                s.AgentServerQqActions = string.Join(",", names);
            }
        }
        if (body["agentServerModel"] is JsonNode asm) s.AgentServerModel = (asm.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentDevices"] is JsonNode ad) s.AgentDevices = ad.GetValue<string>() ?? string.Empty;
        if (body["scenarioPreset"] is JsonNode preset)
        {
            // 只认预设名单里的名字；写别的（或留空）都不放开能力（见 AppSettings.ScenarioPreset）
            s.ScenarioPreset = (preset.GetValue<string>() ?? string.Empty).Trim();
        }

        if (body["enableApprovals"] is JsonNode apv) s.EnableApprovals = apv.GetValue<bool>();
        if (body["approvalApprovers"] is JsonNode aap)
        {
            // 审批人名单：只是一串 QQ 号，限长防呆（真正的身份核验在审批流程里，见 ApprovalStore）
            var list = (aap.GetValue<string>() ?? string.Empty).Trim();
            s.ApprovalApprovers = list.Length > 300 ? list[..300] : list;
        }

        // 参与状态机的上下限（P1）。这里先做一次**面板级**钳制，真正的硬上限在
        // ParticipationPolicy.Clamped()（两处都要过 —— 面板值不可信，代码里那层才是安全边界）。
        if (body["participationMaxConsecutiveReplies"] is JsonNode pmcr)
        {
            s.ParticipationMaxConsecutiveReplies = Math.Clamp(pmcr.GetValue<int>(), 1, 10);
        }

        if (body["participationCooldownSeconds"] is JsonNode pcs)
        {
            s.ParticipationCooldownSeconds = Math.Clamp(pcs.GetValue<int>(), 0, 600);
        }

        if (body["participationProbingMaxReplies"] is JsonNode ppmr)
        {
            s.ParticipationProbingMaxReplies = Math.Clamp(ppmr.GetValue<int>(), 1, 3);
        }

        if (body["participationMaxActiveLifetimeSeconds"] is JsonNode pmal)
        {
            s.ParticipationMaxActiveLifetimeSeconds = Math.Clamp(pmal.GetValue<int>(), 30, 3600);
        }

        if (body["participationMaxExitingLifetimeSeconds"] is JsonNode pmel)
        {
            s.ParticipationMaxExitingLifetimeSeconds = Math.Clamp(pmel.GetValue<int>(), 10, 3600);
        }

        // 两个会真正改变行为的开关（都默认关）：参与闸门、允许提问
        if (body["enableParticipationGating"] is JsonNode epg) s.EnableParticipationGating = epg.GetValue<bool>();
        if (body["enableQuestions"] is JsonNode eq) s.EnableQuestions = eq.GetValue<bool>();

        if (body["enableAgentMask"] is JsonNode eam) s.AgentMaskSensitive = eam.GetValue<bool>();
        if (body["agentPrompt"] is JsonNode ap)
        {
            // 附加提示词：空 = 明确不带（与“没这个字段”不同）；长度设上限，免得一屏文本被贴进每一轮请求
            s.AgentPrompt = (ap.ToString() ?? string.Empty).Trim();
            if (s.AgentPrompt.Length > 8000)
            {
                s.AgentPrompt = s.AgentPrompt[..8000];
            }
        }
        if (body["agentModel"] is JsonNode am2) s.AgentModel = (am2.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentServerBaseUrl"] is JsonNode asbu)
        {
            var v = (asbu.GetValue<string>() ?? string.Empty).Trim();
            // 允许空（= 用聊天那个）；填了就必须是 http(s) 完整地址，不然一次手滑就调不通
            if (v.Length > 0 && (!Uri.TryCreate(v, UriKind.Absolute, out var parsed) ||
                                 (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
            {
                FileLog.Warn("Web", $"服务器 agent 的接口地址无效，已忽略：{v}");
            }
            else
            {
                s.AgentServerBaseUrl = v;
            }
        }
        // 服务器 agent 自己的密钥：与聊天那把同规矩 —— 存 secrets 表、不回显明文、留空 = 不改
        // （要清就去点“清除密钥”，那边有二次确认）
        if (body["agentServerKey"] is JsonValue agentKeyValue && agentKeyValue.TryGetValue<string>(out var rawAgentKey))
        {
            var newAgentKey = (rawAgentKey ?? string.Empty).Trim();
            if (newAgentKey.Length == 0)
            {
                _secrets.SaveAgentServerKey(null);
                s.AgentServerApiKeyOverride = null;
                s.AgentServerApiKey = (Environment.GetEnvironmentVariable("QQCHAT_AGENT_SERVER_KEY") ?? string.Empty).Trim();
            }
            else
            {
                _secrets.SaveAgentServerKey(newAgentKey);
                s.AgentServerApiKeyOverride = newAgentKey;
                s.AgentServerApiKey = newAgentKey;
            }

            // 密钥只记“变了”，绝不回显明文（日志会被贴出来排障）
            FileLog.Write("Web", newAgentKey.Length == 0
                ? "面板清空了服务器 agent 的密钥（回退环境变量）"
                : "面板更新了服务器 agent 的密钥（已掩码保存）");
        }
        if (body["agentServerWorkDir"] is JsonNode asw) s.AgentServerWorkDir = (asw.GetValue<string>() ?? "/data").Trim();
        if (body["agentServerKeepContext"] is JsonNode askc) s.AgentServerKeepContext = askc.GetValue<bool>();
        if (body["agentServerDocker"] is JsonNode asdk) s.AgentServerDocker = asdk.GetValue<bool>();
        if (body["panelDeployEnabled"] is JsonNode pde) s.PanelDeployEnabled = pde.GetValue<bool>();
        if (body["panelDeployUrl"] is JsonNode pdu) s.PanelDeployUrl = (pdu.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentServerMaxSteps"] is JsonNode ass) s.AgentServerMaxSteps = Math.Clamp(ass.GetValue<int>(), 1, 30);
        if (body["agentServerCommandTimeoutSeconds"] is JsonNode asct) s.AgentServerCommandTimeoutSeconds = Math.Clamp(asct.GetValue<int>(), 5, 300);
    }

    /// <summary>报表与模型：健康日报 / 表情包与戳一戳 / 模型地址与密钥 / 心情。</summary>
    private void ApplyReportingAndModelSettings(JsonNode body, AppSettings s, string? newBaseUrl)
    {
            // ---- 服务器健康日报（定时私聊推送）----
            if (body["healthReportEnabled"] is JsonNode hre) s.HealthReportEnabled = hre.GetValue<bool>();
            if (body["healthReportTime"] is JsonNode hrt)
            {
                // 只接受能解析成 HH:mm 的值（"18：00"/"1800" 也认）；解析不出来就保持原值，
                // 免得一次手滑把推送时间静默改成 18:00（用户以为改了 07:30）
                var typed = (hrt.ToString() ?? string.Empty).Trim();
                if (typed.Length > 0)
                {
                    var (hour, minute) = AppSettings.ParseHealthReportClock(typed);
                    var normalized = $"{hour:00}:{minute:00}";
                    var looksValid = typed.Replace('：', ':').Contains(':') || typed.Length == 4;
                    if (looksValid)
                    {
                        s.HealthReportTime = normalized;
                    }
                    else
                    {
                        FileLog.Warn("Web", $"健康日报时刻格式不对，已忽略：{typed}");
                    }
                }
            }
            if (body["healthReportTargets"] is JsonNode hrtg) s.HealthReportTargets = (hrtg.ToString() ?? string.Empty).Trim();

        if (body["enableStickers"] is JsonNode es) s.EnableStickers = es.GetValue<bool>();
            if (body["stickerLibraryMax"] is JsonNode slm) s.StickerLibraryMax = Math.Clamp(slm.GetValue<int>(), 0, 2000);
            if (body["stickerCandidates"] is JsonNode sc) s.StickerCandidates = Math.Clamp(sc.GetValue<int>(), 0, 20);
            if (body["stickerCurateIntervalSeconds"] is JsonNode sci) s.StickerCurateIntervalSeconds = Math.Clamp(sci.GetValue<int>(), 0, 86400);
            if (body["stickerCooldownSeconds"] is JsonNode scl) s.StickerCooldownSeconds = Math.Clamp(scl.GetValue<int>(), 0, 86400);
        if (body["enablePoke"] is JsonNode ep) s.EnablePoke = ep.GetValue<bool>();
        if (body["pokeCooldownSeconds"] is JsonNode pcl) s.PokeCooldownSeconds = Math.Clamp(pcl.GetValue<int>(), 0, 86400);
        if (body["moodTtlSeconds"] is JsonNode mtt) s.MoodTtlSeconds = Math.Clamp(mtt.GetValue<int>(), 0, 86400 * 7);

        // ---- 模型接口（面板可改；留空 = 回退到环境变量）----
        if (newBaseUrl is not null)
        {
            s.ModelBaseUrlOverride = newBaseUrl.Length == 0 ? null : newBaseUrl;
            s.ModelBaseUrl = newBaseUrl.Length > 0
                ? newBaseUrl
                : Environment.GetEnvironmentVariable("QQCHAT_BASE_URL") is { Length: > 0 } envUrl
                    ? envUrl.Trim()
                    : "https://api.openai.com/v1";
        }

        if (body["model"] is JsonNode modelNode)
        {
            var modelName = (modelNode.GetValue<string>() ?? string.Empty).Trim();
            s.ModelOverride = modelName.Length == 0 ? null : modelName;
            s.Model = modelName.Length > 0
                ? modelName
                : Environment.GetEnvironmentVariable("QQCHAT_MODEL") is { Length: > 0 } envModel
                    ? envModel.Trim()
                    : "gpt-4o-mini";
        }

        // 思考档位（快速回复）：开 = 聊天回复换轻量模型（快 2~3 倍；后台活儿不受影响）
        if (body["fastReply"] is JsonNode fr)
        {
            s.FastReply = fr.GetValue<bool>();
        }

        if (body["fastModel"] is JsonValue fm && fm.TryGetValue<string>(out var fastName))
        {
            // 留空就留空：快速档没填模型时不生效（仍旧用主模型），别自作主张塞一个具体模型名
            // —— 具体名字跟着中转走，写死只会让人以为“开关没生效”。
            s.FastModel = (fastName ?? string.Empty).Trim();
        }

        // 密钥：存 data/secrets.json（权限 600），**不写 settings.json**；留空 = 删掉、回退环境变量
        if (body["apiKey"] is JsonValue keyValue && keyValue.TryGetValue<string>(out var rawKey))
        {
            var newKey = (rawKey ?? string.Empty).Trim();
            if (newKey.Length == 0)
            {
                _secrets.SaveApiKey(null);
                s.ApiKeyOverride = null;
                s.ApiKey = (Environment.GetEnvironmentVariable("QQCHAT_API_KEY") ??
                            Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty).Trim();
            }
            else
            {
                _secrets.SaveApiKey(newKey);
                s.ApiKeyOverride = newKey;
                s.ApiKey = newKey;
            }

            // 密钥只记“变了”，绝不回显明文（日志会被贴出来排障）
            FileLog.Write("Web", newKey.Length == 0 ? "面板清空了模型 API Key（回退环境变量）" : "面板更新了模型 API Key（已掩码保存）");
        }

        // 心情是机器人状态（不是行为配置），单独走它的存储：留空 = 交回自动描述。
        // 日志口径与 BotAgentHost 时期逐字一致（走 PanelNotifier.EmitLog → [Agent] 标签）。
        if (body["mood"] is JsonValue moodValue && moodValue.TryGetValue<string>(out var moodText))
        {
            var moodNow = Clock.Now;
            if (string.IsNullOrWhiteSpace(moodText))
            {
                _mood.Reset(moodNow);
                _ui.EmitLog("心情已交回自动描述（按被戳次数）");
            }
            else if (_mood.SetText(moodText, moodNow))
            {
                _ui.EmitLog($"心情被手动改成：{_mood.Describe(moodNow)}");
            }
        }
    }

    private JsonObject BuildSettingsPayload()

    {
        var s = _settings;

        return new JsonObject
        {
            ["runtime"] = new JsonObject
            {
                ["botPersona"] = s.BotPersona,
                ["messageWhitelist"] = s.MessageWhitelist,
        ["whitelistGroups"] = s.WhitelistGroups,
        ["whitelistPrivates"] = s.WhitelistPrivates,
        // 留空的那一边会回落到旧的共用名单 —— 面板要如实告知，不然号主会以为新框填了没生效
        ["whitelistGroupsFromLegacy"] = string.IsNullOrWhiteSpace(s.WhitelistGroups) && s.MessageWhitelist.Length > 0,
        ["whitelistPrivatesFromLegacy"] = string.IsNullOrWhiteSpace(s.WhitelistPrivates) && s.MessageWhitelist.Length > 0,
                ["aiDesire"] = s.AiDesire,
                ["fastReply"] = s.FastReply,
                ["fastModel"] = s.FastModel,
                ["replyModel"] = s.ReplyModel,
                ["suitabilityThreshold"] = s.SuitabilityThreshold,
                ["aiModeEnabled"] = s.AiModeEnabled,
                ["maxTokens"] = s.MaxTokens,
                ["groupCooldownSeconds"] = s.GroupCooldownSeconds,
                ["privateCooldownSeconds"] = s.PrivateCooldownSeconds,
                ["idleFallbackSeconds"] = s.IdleFallbackSeconds,
                ["splitReplies"] = s.SplitReplies,
                ["enableProactive"] = s.EnableProactive,
                ["proactiveCooldownSeconds"] = s.ProactiveCooldownSeconds,
                ["proactiveQuietSeconds"] = s.ProactiveQuietSeconds,
                ["ignoreBracketMessages"] = s.IgnoreBracketMessages,
                ["segmentDelayMs"] = s.SegmentDelayMs,
                ["maxContextMessages"] = s.MaxContextMessages,
                ["profileLookupCount"] = s.ProfileLookupCount,
                ["profileSummaryLines"] = s.ProfileSummaryLines,
                ["maxProfileChars"] = s.MaxProfileChars,
                ["maxMessagesPerConversation"] = s.MaxMessagesPerConversation,
                ["maxConcurrentReplies"] = s.MaxConcurrentReplies,
                ["enableProfileSummary"] = s.EnableProfileSummary,
                ["profileSummaryThreshold"] = s.ProfileSummaryThreshold,
                ["profileSummaryMaxChars"] = s.ProfileSummaryMaxChars,
                ["profileSummaryIntervalSeconds"] = s.ProfileSummaryIntervalSeconds,
                ["enableVoice"] = s.EnableVoice,
        ["voiceName"] = s.VoiceName,
        ["voiceSpeed"] = s.VoiceSpeed,
            ["voicePitch"] = s.VoicePitch,
            ["voiceVol"] = s.VoiceVol,
            ["voiceEmotion"] = s.VoiceEmotion,
        ["voiceMaxChars"] = s.VoiceMaxChars,
        ["voiceEagerness"] = s.VoiceEagerness,
        ["ttsServiceUrl"] = s.TtsServiceUrl,
        ["ttsProvider"] = s.TtsProvider,
        ["ttsApiBase"] = s.TtsApiBase,
        ["ttsModel"] = s.TtsModel,
        ["ttsKeyConfigured"] = !string.IsNullOrWhiteSpace(_secrets.LoadTtsKey()),
        ["ttsKeyMasked"] = MaskSecret(_secrets.LoadTtsKey()),

        // 官方通道（QQ 开放平台）：与私域并存，两边会话/上下文/白名单互不串台。
        // secret 不在这里回（密钥只从环境变量读，面板不回显）。
        ["officialEnabled"] = s.OfficialEnabled,
        ["officialAppId"] = s.OfficialAppId,
        ["officialSecretConfigured"] = !string.IsNullOrWhiteSpace(s.OfficialAppSecret),
        ["officialSecretMasked"] = MaskSecret(s.OfficialAppSecret),
        ["officialSecretSource"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QQCHAT_OFFICIAL_APP_SECRET"))
            ? "env"
            : (string.IsNullOrWhiteSpace(_secrets.LoadOfficialSecret()) ? "none" : "panel"),
        ["officialSandbox"] = s.OfficialSandbox,
        ["officialWhitelistGroups"] = s.OfficialWhitelistGroups,
        ["officialWhitelistPrivates"] = s.OfficialWhitelistPrivates,
        ["channels"] = BuildChannelStatus(),
        // 对话总开关（分通道静音）：与顶部那个全局 AI 开关不同，这里能只关一条通道。
        ["privateChatEnabled"] = s.PrivateChatEnabled,
        ["officialChatEnabled"] = s.OfficialChatEnabled,
        // 官方通道**见过的会话**（别名号 + 名字）——面板上点一下就能填进官方白名单，
        // 不必再让人去猜“别名号长什么样”（填真实号 = 官方通道静默全拦，今天刚踩过）。
        ["officialConversations"] = BuildOfficialConversations(),
         ["enableWebSearch"] = s.EnableWebSearch,
         ["webSearchUseModelSearch"] = s.WebSearchUseModelSearch,
         ["webSearchSources"] = s.WebSearchSources,
         ["webSearchMaxResults"] = s.WebSearchMaxResults,
         ["webSearchCooldownSeconds"] = s.WebSearchCooldownSeconds,
         ["webSearchTimeoutSeconds"] = s.WebSearchTimeoutSeconds,
        ["enableLinkPreview"] = s.EnableLinkPreview,
        ["linkPreviewTimeoutSeconds"] = s.LinkPreviewTimeoutSeconds,
        ["linkPreviewMax"] = s.LinkPreviewMax,
        ["enableMusic"] = s.EnableMusic,
        ["neteaseBaseUrl"] = s.NeteaseBaseUrl,
        ["musicSources"] = s.MusicSources,
        ["musicBitrate"] = s.MusicBitrate,
        ["musicMaxDownloadMb"] = s.MusicMaxDownloadMb,
        ["musicMaxAnalysisSeconds"] = s.MusicMaxAnalysisSeconds,
        ["musicLibraryMax"] = s.MusicLibraryMax,
        ["musicNoteTtlDays"] = s.MusicNoteTtlDays,
        ["musicListenCooldownSeconds"] = s.MusicListenCooldownSeconds,
        ["musicUnderstandModel"] = s.MusicUnderstandModel,
        ["musicSendAudioToModel"] = s.MusicSendAudioToModel,
        ["musicAudioToModelMaxKb"] = s.MusicAudioToModelMaxKb,
        ["musicKeepAudio"] = s.MusicKeepAudio,
        ["enableAgentBridge"] = s.EnableAgentBridge,
        ["agentPrefix"] = s.AgentPrefix,
        ["agentAllowedUsers"] = s.AgentAllowedUsers,
        ["agentWorkDir"] = s.AgentWorkDir,
        ["agentModel"] = s.AgentModel,
        ["agentTools"] = s.AgentTools,
        ["agentTimeoutSeconds"] = s.AgentTimeoutSeconds,
        ["agentReplyMaxChars"] = s.AgentReplyMaxChars,
        ["agentProgressSeconds"] = s.AgentProgressSeconds,
        ["agentMaxQueued"] = s.AgentMaxQueued,
        ["agentTarget"] = s.AgentTarget,
        ["enableServerAgent"] = s.EnableServerAgent,
        ["enableHostAgent"] = s.EnableHostAgent,
        ["hostAgent"] = s.EnableHostAgent,
        ["agentServerTools"] = s.AgentServerTools,
        ["agentServerQqActions"] = s.AgentServerQqActions,
        ["agentServerQqActionsEffective"] = QqActionCatalog.Summarize(QqActionCatalog.ParseAllowed(s.AgentServerQqActions)),
        ["agentServerModel"] = s.AgentServerModel,
        ["agentModel"] = s.AgentModel,
        ["agentServerBaseUrl"] = s.AgentServerBaseUrl,
        // 服务器 agent 的密钥：只给“设没设 / 掩码 / 来源”，不回显明文（与聊天那把 key 同样的规矩）
        ["agentServerKeySet"] = !string.IsNullOrWhiteSpace(s.AgentServerApiKey),
        ["agentServerKeyMasked"] = Mask(s.AgentServerApiKey),
        ["agentServerKeySource"] = !string.IsNullOrWhiteSpace(s.AgentServerApiKeyOverride)
            ? "panel"
            : (string.IsNullOrWhiteSpace(s.AgentServerApiKey) ? "none" : "env"),
        ["agentDevices"] = s.AgentDevices,
        ["scenarioPreset"] = s.ScenarioPreset,
        ["scenarioPresets"] = new JsonArray(Domain.Permissions.ScenarioPresets.All
            .Select(name => (JsonNode)name!).ToArray()),
        ["scenarioCapabilities"] = s.ScenarioPreset.Length == 0
            ? "(跟随现有开关)"
            : Domain.Permissions.ChatCapabilitySet.FromSwitches(
                s.EnableWebSearch, s.EnableMusic, s.EnableVoice, s.EnableStickers, s.EnablePoke,
                s.ScenarioPreset).Describe(),
        ["enableApprovals"] = s.EnableApprovals,
        ["approvalApprovers"] = s.ApprovalApprovers,
        // 面板要能说清“开了审批会发生什么”：只覆盖这一个固定假工具，且它没有任何真实副作用
        ["approvalTool"] = Domain.Permissions.ApprovalFlow.FixedToolId,
        ["approvalTtlSeconds"] = Domain.Permissions.ApprovalFlow.TtlSeconds,
        ["approvalCapabilities"] = s.EnableApprovals
            ? Domain.Permissions.ChatCapabilitySet.FromSwitches(
                s.EnableWebSearch, s.EnableMusic, s.EnableVoice, s.EnableStickers, s.EnablePoke,
                s.ScenarioPreset, approvalsEnabled: true).Describe()
            : "(审批关闭：模型写的 action=tool 一律安全静默)",
        // 参与状态机（P1）：参数可改，但**只影响观测日志**（gating 未打开）
        ["participationMaxConsecutiveReplies"] = s.ParticipationMaxConsecutiveReplies,
        ["participationCooldownSeconds"] = s.ParticipationCooldownSeconds,
        ["participationProbingMaxReplies"] = s.ParticipationProbingMaxReplies,
        ["participationMaxActiveLifetimeSeconds"] = s.ParticipationMaxActiveLifetimeSeconds,
        ["participationMaxExitingLifetimeSeconds"] = s.ParticipationMaxExitingLifetimeSeconds,
        ["participationPolicy"] = DescribeParticipationPolicy(s),
        ["enableParticipationGating"] = s.EnableParticipationGating,
        ["participationGating"] = s.EnableParticipationGating
            ? "on（闸门开启：状态机说这一轮不参与就不叫模型 —— 只收不放）"
            : "off（只观测：不改“发不发”的判定）",
        ["enableQuestions"] = s.EnableQuestions,
        ["questionTool"] = Domain.Permissions.ApprovalFlow.QuestionToolId,
        ["questionCapabilities"] = s.EnableQuestions
            ? "允许提问（带一次性编号与有效期；不授予任何权限）"
            : "(提问关闭：模型写的 action=ask 一律安全静默)",
        ["enableAgentMask"] = s.AgentMaskSensitive,
        ["agentPrompt"] = s.AgentPrompt,
        // 面板「恢复默认」按钮用：默认那份写在 AppSettings.DefaultAgentPrompt（只有一处真源）
        ["agentPromptDefault"] = AppSettings.DefaultAgentPrompt,
        ["agentServerWorkDir"] = s.AgentServerWorkDir,
        ["agentServerKeepContext"] = s.AgentServerKeepContext,
        ["agentServerDocker"] = s.AgentServerDocker,
        ["panelDeployEnabled"] = s.PanelDeployEnabled,
        ["panelDeployUrl"] = s.PanelDeployUrl,
        ["agentServerMaxSteps"] = s.AgentServerMaxSteps,
        ["agentServerCommandTimeoutSeconds"] = s.AgentServerCommandTimeoutSeconds,
        ["neteaseCookieSet"] = !string.IsNullOrWhiteSpace(s.NeteaseCookie),
        ["enableStickers"] = s.EnableStickers,
                ["stickerLibraryMax"] = s.StickerLibraryMax,
                ["stickerCandidates"] = s.StickerCandidates,
                ["stickerCurateIntervalSeconds"] = s.StickerCurateIntervalSeconds,
                ["stickerCooldownSeconds"] = s.StickerCooldownSeconds,
        ["enablePoke"] = s.EnablePoke,
        ["pokeCooldownSeconds"] = s.PokeCooldownSeconds,
        ["moodTtlSeconds"] = s.MoodTtlSeconds,
        // ---- 服务器健康日报（定时私聊推送）----
        ["healthReportEnabled"] = s.HealthReportEnabled,
        ["healthReportTime"] = s.HealthReportTime,
        ["healthReportTargets"] = s.HealthReportTargets,
        // 当前心情（可手改；空 = 由代码按被戳次数自动描述）
        ["mood"] = _mood.CurrentText(Clock.Now) ?? string.Empty,
        ["moodSummary"] = _mood.Describe(Clock.Now)
            },
            // 只读：容器环境变量职责，改这里无效（见 BuildEnvPayload）
            ["env"] = BuildEnvPayload(s),
            ["settingsFile"] = _settingsRepo.FilePath
        };
    }

    /// <summary>
    /// 只读的「容器环境变量职责」那一段（模型端点 / 协议端地址 / 数据目录 / 端口 / 时区）：
    /// 面板只能看不能改 —— 改这些要动 compose 的环境变量。拆出来是因为主 payload 原本三百多行。
    /// </summary>
    private JsonObject BuildEnvPayload(AppSettings s) => new()
    {
        ["modelBaseUrl"] = s.ModelBaseUrl,
        ["modelBaseUrlSource"] = string.IsNullOrWhiteSpace(s.ModelBaseUrlOverride) ? "env" : "panel",
        ["model"] = s.Model,
        ["modelSource"] = string.IsNullOrWhiteSpace(s.ModelOverride) ? "env" : "panel",
        ["fastReply"] = s.FastReply,
        ["fastModel"] = s.FastModel,
        ["replyModel"] = s.ReplyModel,
        ["maxTokens"] = s.MaxTokens,
        ["apiKeyMasked"] = Mask(s.ApiKey),
        ["apiKeySet"] = !string.IsNullOrWhiteSpace(s.ApiKey),
        ["apiKeySource"] = !string.IsNullOrWhiteSpace(s.ApiKeyOverride) ? "panel" : (string.IsNullOrWhiteSpace(s.ApiKey) ? "none" : "env"),
        ["oneBotProtocol"] = s.OneBotProtocol,
        ["oneBotAddress"] = s.OneBotAddress,
        ["oneBotTokenMasked"] = Mask(s.OneBotToken),
        ["uin"] = s.NormalizedUin,
        ["dataDir"] = AppPaths.RuntimeRoot,
        ["healthPort"] = s.HealthPort,
        ["tz"] = Environment.GetEnvironmentVariable("TZ") ?? TimeZoneInfo.Local.Id
    };


    private static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        return secret.Length <= 4 ? "****" : secret[..4] + "****";
    }
}
