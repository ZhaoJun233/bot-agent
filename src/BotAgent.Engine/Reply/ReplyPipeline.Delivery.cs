using BotAgent.Services.Plugins;
using BotAgent.Services.Voice;
using BotAgent.Engine.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Stickers;
using BotAgent.Services.Agent;
using BotAgent.Services.Platforms;
using BotAgent.Services.Ports;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{
    /// <summary>
    /// 后台真去搜一次，把结果留给下一轮（并在允许时叫醒模型）。
    /// 为什么要冷却：搜索是一次真实的模型调用 + 几秒等待；群里连问几个问题就排队了。
    /// </summary>
    private void QueueWebSearchAsync(BotConversation conversation, string query, AppSettings snapshot, EffectivePlatformPolicy platformPolicy)
    {
        var key = conversation.SourceKey;
        if (!QueuedResearchAllowed(snapshot, platformPolicy, "web.search")) return;
        if (!_research.TryBeginSearch(key, snapshot.WebSearchCooldownSeconds, out var searchWhy))
        {
            _hooks.Log($"[Search] 这次不搜（{searchWhy}）：{query}");
            return;
        }

        _hooks.Log($"[Search] 模型想搜「{query}」");
        QueueBackground("web-search:" + key + ":" + query, "web-search:" + key + ":" + query, async ct =>
        {
            if (ct.IsCancellationRequested || !QueuedResearchAllowed(snapshot, platformPolicy, "web.search")) return;
            try
            {
                var result = await _research.SearchAsync(key, query, ct);
                if (ct.IsCancellationRequested || !QueuedResearchAllowed(snapshot, platformPolicy, "web.search")) return;
                if (result is not null && !result.HasContent)
                {
                    _hooks.Log($"[Search] 没搜到「{query}」：{result.Error}");
                    // P1 观测：模型点名的工具没拿到东西 → 记一次失败（只降级不升级）
                    _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
                }

                // 搜到了就给它一次开口机会（没搜到也给 —— 让它能如实说“没查到”）
                RequestReply(conversation, null);
            }
            catch (OperationCanceledException)
            {
                // 停机/超时取消：不再把结果当成失败去降级。
            }
            catch (Exception ex)
            {
                _hooks.Log($"[Search] 搜「{query}」失败: {ex.Message}");
                _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
            }
        });
    }
    /// <summary>后台读一个网页的正文（模型填 read 时），留给下一轮。</summary>
    private void QueuePageReadAsync(BotConversation conversation, string url, AppSettings snapshot, EffectivePlatformPolicy platformPolicy)
    {
        var key = conversation.SourceKey;
        if (!QueuedResearchAllowed(snapshot, platformPolicy, "web.read")) return;
        _hooks.Log($"[Search] 模型想读页面 {TextRules.Shorten(url, 80)}");
        QueueBackground("web-read:" + key + ":" + url, "web-read:" + key + ":" + url, async ct =>
        {
            if (ct.IsCancellationRequested || !QueuedResearchAllowed(snapshot, platformPolicy, "web.read")) return;
            try
            {
                var text = await _research.ReadPageAsync(key, url, ct);
                if (ct.IsCancellationRequested || !QueuedResearchAllowed(snapshot, platformPolicy, "web.read")) return;
                if (text is null)
                {
                    // P1 观测：读页面失败（工具失败 → 只降级）
                    _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
                }

                RequestReply(conversation, null);
            }
            catch (OperationCanceledException)
            {
                // 停机/超时取消：不再把结果当成失败去降级。
            }
            catch (Exception ex)
            {
                _hooks.Log($"[Search] 读页面失败: {ex.Message}");
                _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
            }
        });
    }
    private bool QueuedResearchAllowed(AppSettings snapshot, EffectivePlatformPolicy platformPolicy, string toolId)
        => _replyTools.Allows(snapshot, platformPolicy, toolId)
            && _replyTools.Allows(_settings, platformPolicy, toolId);


    /// <summary>
    /// 第 4 步的**动作**那一半：搜索 / 读页 / 听歌 / 分享歌（都是两轮动作，后台去跑、下一轮再开口）
    /// 加上表情包候选的校验（不在库里、没过审、频率门都要挡掉）。
    /// 每个动作都要过服务端能力闸门（P3：模型输出不构成授权）；返回这一轮真正要发的表情包（可能为 null）。
    /// </summary>
    private async Task<StickerRecord?> QueueTurnActionsAsync(
         BotConversation conversation,
         AppSettings snapshot,
         Domain.Permissions.ChatCapabilitySet caps,
         EffectivePlatformPolicy platformPolicy,
         CompletionResult result,
        bool directAddress)
    {
        await Task.CompletedTask;
        if (_riskBackoff?.IsActive(conversation.SourceKey) == true)
        {
            _hooks.Log($"协议端风控退避中，暂停语音、表情包、音乐和主动动作（会话={Channels.Describe(conversation.SourceKey)}）");
            return null;
        }

        // 表情包：模型可以只发图不说话，也可以“文字 + 图”。
        // 校验一下 id（模型偶发会编造/多空格），拿不到就把这次当成纯文字。
        StickerRecord? sticker = null;
        // 联网搜索（search / read）：后台去查，拿到结果后再给它一次开口的机会。
        // 这两个是“两轮动作”—— 模型这轮照常接话（reply 可以写“我去查查”），下一轮拿着事实说。
        // search 优先于 read：模型一般只会填一个。
        if (platformPolicy.Feature("websearch", snapshot.EnableWebSearch).Enabled && AllowsFeature(PluginGate.WebSearchCapability) && _research.IsReady && result.Search is { Length: > 0 } wantedQuery)
        {
            // P3：联网是“真出网”，必须过服务端能力闸门（模型输出不构成授权）；策略用本轮快照
            if (_approvals.AllowCapability(conversation, "web.search", wantedQuery, out _, pinned: caps))
            {
                QueueWebSearchAsync(conversation, wantedQuery, snapshot, platformPolicy);
            }
        }
        else if (platformPolicy.Feature("websearch", snapshot.EnableWebSearch).Enabled && AllowsFeature(PluginGate.WebSearchCapability) && _research.IsReady && result.Read is { Length: > 0 } pageUrl)
        {
            if (_approvals.AllowCapability(conversation, "web.read", pageUrl, out _, pinned: caps))
            {
                QueuePageReadAsync(conversation, pageUrl, snapshot, platformPolicy);
            }
        }

        // 模型想听一首歌（listen 字段）：后台去搜、去听，听完再给它一次开口的机会。
        // 这是群里说“去听一下 XXX”的唯一入口 —— 不靠正则猜句子，交给模型自己决定。
        // P3（V3 §9.2）：听歌是“真出网”（去外部音乐服务搜歌 + 拉音频），必须过能力闸门 ——
        // 不能因为它是“老入口”就绕过场景白名单与预算。
        if (platformPolicy.Feature("music", snapshot.EnableMusic).Enabled && AllowsFeature(PluginGate.MusicCapability) && result.Listen is { Length: > 0 } wantedSong && _music is not null
            && _approvals.AllowCapability(conversation, "music.listen", wantedSong, out _, pinned: caps))
        {
            var key = conversation.SourceKey;
            if (_music.TryBeginListen(key, snapshot.MusicListenCooldownSeconds, out var listenWhy))
            {
                _hooks.Log($"[Music] 模型想听「{wantedSong}」");
                QueueBackground("music-listen:" + key + ":" + wantedSong, "music-listen:" + key + ":" + wantedSong, async ct =>
                {
                    if (!QueuedMusicStillAllowed(platformPolicy, "听歌")) return;
                    var note = await _music.ListenAsync(key, wantedSong, "群友", ct);
                    if (ct.IsCancellationRequested) return;
                    if (string.IsNullOrWhiteSpace(note))
                    {
                        // P1 观测：模型点名的“听歌”没做成 → 工具失败
                        _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
                        return;
                    }

                    RequestReply(conversation, null);
                });
            }
            else
            {
                _hooks.Log($"[Music] 「{wantedSong}」还在冷却中（{listenWhy}）");
            }
        }

        // 模型想把某首歌分享给群里 → 搜到就发一张网易云卡片，顺手“听”一遍（下一轮它就能聊这首歌）。
        // P3（V3 §9.2）：分享歌曲 = 往当前会话发额外消息，必须过同一条闸门（未配场景时行为不变）。
        if (platformPolicy.Feature("music", snapshot.EnableMusic).Enabled && AllowsFeature(PluginGate.MusicCapability) && result.ShareSong is { Length: > 0 } songToShare && _music is not null
            && _approvals.AllowCapability(conversation, "music.share", songToShare, out _, pinned: caps))
        {
            var key = conversation.SourceKey;
            var (shareIsGroup, shareTargetId) = conversation.Target;
            QueueBackground("music-share:" + key + ":" + songToShare, "music-share:" + key + ":" + songToShare, async ct =>
            {
                if (!QueuedMusicStillAllowed(platformPolicy, "分享")) return;
                var shared = await _music.ShareAsync(songToShare, shareIsGroup, shareTargetId, ct);
                if (!shared)
                {
                    _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
                    return;
                }

                // 卡片发出去了，接着真去听一遍：下一轮发言时它就“听过这首歌”。
                // 第二个副作用开始前再复核一次：排队/分享期间插件可能被关、会话可能已停机。
                if (!QueuedMusicStillAllowed(platformPolicy, "分享后听歌")) return;
                _ = await _music.ListenAsync(key, songToShare, "（自己分享的）", ct);
            });
        }

        if (platformPolicy.Feature("stickers", snapshot.EnableStickers).Enabled && AllowsFeature(PluginGate.StickersCapability) && result.StickerId is { } sid && !_vibes.IsSober(conversation.SourceKey)
            && _approvals.AllowCapability(conversation, "sticker.send", null, out _, pinned: caps))
        {
            sticker = _stickers.Store.Find(sid);
            if (sticker is null)
            {
                _hooks.Log($"模型挑的表情包 #{sid} 不在库里，已忽略（只发文字）");
            }
            else if (sticker.IsSticker != true)
            {
                // 还没通过“是不是表情包”审核的图不当表情包用：
                // 宁可这一轮不发，也不要把聊天截图/广告发出去
                _hooks.Log($"这张 #{sid} 还没通过“是不是表情包”审核，本轮不发");
                sticker = null;
            }
            else if (!AllowSticker(conversation, sticker.Id, out var stickerWhy))
            {
                // 频率门：库小的时候模型会每句都挂同一张（群里直接开愤：
                // “你别老是发这个表情包了”）。表情包是调味品，不是主食。
                _hooks.Log($"这次不发表情包（{stickerWhy}）: #{sticker.Id} {StickerText.Describe(sticker)}");
                sticker = null;
            }
        }
        return sticker;
    }

    /// <summary>
    /// 后台作业真正开始前的通用实时复核：入队时检查只决定“是否值得排队”，
    /// 排队期间插件、平台功能或来源租约都可能被关闭；这些作业不能在关闭后继续发起外部 I/O。
    /// </summary>
    private bool QueuedInboundCapabilityAllowed(
        string capability,
        string feature,
        bool fallbackEnabled,
        EffectivePlatformPolicy turnPolicy,
        string phase)
    {
        if (!AllowsFeature(capability))
        {
            _hooks.Log($"[媒体] 后台{phase}已取消（插件已关闭）");
            return false;
        }

        if (PlatformSourceExecution.Current is { IsCurrent: false })
        {
            _hooks.Log($"[媒体] 后台{phase}已取消（来源已退休）");
            return false;
        }

        var policy = _platformPolicies.Resolve(turnPolicy.Context);
        var enabled = policy.Registered && policy.Connected && policy.Enabled && policy.ChatEnabled
            && policy.Feature(feature, fallbackEnabled).Enabled;
        if (!enabled)
        {
            _hooks.Log($"[媒体] 后台{phase}已取消（平台或功能已关闭）");
        }

        return enabled;
    }

    /// <summary>
    /// 后台音乐作业开始（以及串起第二个副作用）之前的实时复核：
    /// 插件、功能、平台与会话都必须仍然有效，排队期间被关掉的作业不能再发起新的出网/发送。
    /// </summary>
    private bool QueuedMusicStillAllowed(EffectivePlatformPolicy turnPolicy, string phase)
    {
        if (!AllowsFeature(PluginGate.MusicCapability) || _music is null)
        {
            _hooks.Log($"[Music] 后台{phase}已取消（插件或服务已关闭）");
            return false;
        }

        // 用本轮的真实平台上下文重新解析：不能用 channel-only 猜测，
        // 新配置下 channel 没有实例身份，会被判成 source_context_required。
        if (PlatformSourceExecution.Current is { IsCurrent: false })
        {
            _hooks.Log($"[Music] 后台{phase}已取消（来源已退休）");
            return false;
        }

        var policy = _platformPolicies.Resolve(turnPolicy.Context);
        var enabled = policy.Registered && policy.Connected && policy.Enabled && policy.ChatEnabled
            && policy.Feature("music", _settings.EnableMusic).Enabled;
        if (!enabled)
        {
            _hooks.Log($"[Music] 后台{phase}已取消（平台或功能已关闭）");
        }

        return enabled;
    }

    /// <summary>
    /// 语音那条路（第 4 步之一）：过技术性限制（开关 / 字数上限 / 同会话频率下限 / 能力闸门）→ 拼 /speak URL
    /// → 逐段交给协议端发。哪段没发出去就把它记下来当文字补（内容不能丢）。
    /// 「该不该发语音」**不由这里判断** —— 那是模型的事（管理员 2026-09-21 明确过）。
    /// </summary>
    private async Task<(bool VoiceSent, List<string> VoiceFailed)> TrySendVoiceAsync(
         BotConversation conversation,
         AppSettings snapshot,
         Domain.Permissions.ChatCapabilitySet caps,
         EffectivePlatformPolicy platformPolicy,
        List<string> voiceParts,
        string voiceText,
        bool isGroup,
        long targetId,
        CompletionResult result,
        bool directAddress)
    {
        if (_riskBackoff?.IsActive(conversation.SourceKey) == true)
        {
            return (false, new List<string>());
        }

        // ───── 语音（模型填了 speak）─────
        // 怎么发：只把 TTS 的 /speak URL 交给协议端，让 NapCat 自己去下载 → 转 silk → 上传
        // （见 IPlatformHistorySource.SendVoiceAsync）—— 机器人这边不碰音频编码。
        // 为什么得克制：合成要几秒 CPU、音频占流量、群里语音连发就是刷屏。
        // 提示词让它“偶尔用”，代码侧再加一道同会话 45 秒的闸门。
        string? voiceUrl = null;
        var voiceUrls = new List<string>();
        string? voiceSkipWhy = null;
        if (voiceParts.Count > 0)
        {
            // 2026-09-21（管理员要求“什么时候发语音让模型自己定”）：这里以前还有一道
            // “气氛沉（有人低落/在吵架）就一律不发语音”的硬拦，已删——那本来就是判断类的事，
            // 现在只把气氛（vibeHint）递给模型看，由它自己权衡。
            // 代码侧只留“技术性”限制：开关、字数上限（云端/协议端真有上限）、同会话频率下限（防刷屏）。
            var maxChars = Math.Clamp(snapshot.VoiceMaxChars, 10, 300);
            if (!platformPolicy.Feature("voice", snapshot.EnableVoice).Enabled || !AllowsFeature(PluginGate.VoiceCapability) || _voice is null)
            {
                voiceSkipWhy = "语音消息开关是关的";
            }
            else if (!_approvals.AllowCapability(conversation, "voice.speak", null, out var voiceCapWhy, pinned: caps))
            {
                voiceSkipWhy = $"能力闸门拒绝（{voiceCapWhy}）";
            }
            else if (voiceParts.FirstOrDefault(p => p.Length > maxChars) is { } longPart)
            {
                voiceSkipWhy = $"{longPart.Length} 字超过上限 {maxChars}";
            }
            else if (!AllowVoice(conversation, out var voiceReason))
            {
                voiceSkipWhy = voiceReason;
            }
            // 语速/情绪/音调**由模型按语境自己定**（管理员 2026-09-21）：它给了就用它的，
            // 没给就退回面板里那三个默认值；面板值仍受同样范围限制。
            else
            {
                // 拼 /speak URL（第 2、3 段同一套语气参数）：细节在 IVoiceUseCase，这里只管编排
                voiceUrls = _voice.BuildSpeakUrls(voiceParts, result.VoiceEmotion, result.VoiceSpeed, result.VoicePitch);
                if (voiceUrls.Count == 0)
                {
                    voiceSkipWhy = "TTS 服务地址没配置（应形如 http://tts:5000）";
                }
                else
                {
                    voiceUrl = voiceUrls[0];
                }
            }
        }

        var voiceSent = false;
        var voiceFailed = new List<string>();
        if (voiceUrls.Count > 0)
        {
            // 模型把话切成了几段 → 每段一条语音条（≤3 条）。
            // 哪段没发出去，就把那段当文字补上（内容不能丢）。
            for (var i = 0; i < voiceUrls.Count; i++)
            {
                var part = i < voiceParts.Count ? voiceParts[i] : voiceText;
                var ok = await _source.SendVoiceAsync(isGroup, targetId, voiceUrls[i]);
                if (!ok)
                {
                    if (part.Length > 0) voiceFailed.Add(part);
                    _hooks.Log(voiceUrls.Count > 1
                        ? $"[Voice] 第 {i + 1}/{voiceUrls.Count} 段没发出去 → 这段改发文字"
                        : "[Voice] 语音没发出去 → 改发文字（具体原因见上一行的 retcode/响应体）");
                    continue;
                }

                voiceSent = true;
                _voice?.MarkSent(conversation.SourceKey);
                // 把模型给的语气参数也记下来 —— 不然“它到底有没有按语境调情绪”没法验证
                var tone = new List<string>();
                if (!string.IsNullOrWhiteSpace(result.VoiceEmotion)) tone.Add("情绪 " + result.VoiceEmotion);
                if (result.VoiceSpeed is double ms) tone.Add($"语速 {ms:0.##}");
                if (result.VoicePitch is int mp) tone.Add($"音调 {mp:+#;-#;0}");
                var voiceName = _voice?.ClientVoiceName ?? "默认";
                _hooks.Log($"[Voice] 已发语音{(voiceUrls.Count > 1 ? $"（第 {i + 1}/{voiceUrls.Count} 段）" : string.Empty)}"
                        + $"（{part.Length} 字，音色 {voiceName}"
                        + (tone.Count > 0 ? "，模型定的 " + string.Join('/', tone) : "，模型未指定语气（用面板默认）")
                        + $"）：{TextRules.Shorten(part, 40)}");
            }
        }
        else if (voiceSkipWhy is not null)
        {
            _hooks.Log($"[Voice] 模型想用语音说，但{voiceSkipWhy} → 改发文字");
        }

        return (voiceSent, voiceFailed);
    }

    /// <summary>
    /// 一轮回复的**发送与记账**（第 5 步）：语音/文字去重 → 写进上下文 → 实际发送（文字/图/戳）→ 喂参与状态机 → 写运行日志。
    /// 不变量：**引用只挂第一条**（文字 + 图时图不带引用）、**语音/文字去重**（同一句不重复发）、
    /// 戳一戳要过三道门（号码出现过 / 心情 / 频率 + 能力闸门）。
    /// 拆出来只为了可读性：这里的一行都没改语义 —— 位置、顺序、日志措辞都是原来那句。
    /// </summary>
    private async Task SendTurnAsync(
         BotConversation conversation,
         IReadOnlyList<ChatMessage> context,
         long? triggerMessageId,
         Domain.Permissions.ChatCapabilitySet caps,
         EffectivePlatformPolicy platformPolicy,
        bool isGroup,
        long targetId,
        long? replyTo,
        string reply,
        string voiceText,
        List<string> voiceParts,
        List<string> voiceFailed,
        bool voiceSent,
        StickerRecord? sticker,
        long? pokeTarget,
        double elapsed,
        CompletionResult result,
        bool directAddress)
    {
        // 到底还发不发文字：
        //   • 语音发成功了、且 reply 就是那句话（或没写 reply）→ 不再重复发同一句；
        //   • 语音发成功了、但 reply 另写了内容 → 那是模型自己想补的话，照发；
        //   • 语音没发出去 → 至少把要说的话当文字发出去。
        var textReply = reply.Length > 0 ? reply : null;
        if (voiceParts.Count > 0)
        {
            if (voiceSent)
            {
                // 去重（2026-09-21 修）：以前只在**一字不差**时才吞掉文字 ✗ —— speak「好呀，那我们八点见」
                // 配 reply「好呀八点见！」就会语音+文字把同一句发两遍 ✗。现在按“去标点后是否同一句 /
                // 是否互相包含”判断。
                var said = string.Join(" ", voiceParts.Where(p => !voiceFailed.Contains(p)));
                if (textReply is not null && TextRules.SameSaid(textReply, said))
                {
                    _hooks.Log($"[Voice] 语音与文字是同一句（{TextRules.Shorten(textReply, 24)}）→ 不再重复发文字");
                    textReply = null;
                }
                else if (textReply is not null && said.Length > 0)
                {
                    // 口径（2026-09-21 第三次定）：**看不看这段文字由模型说了算** ——
                    // 它在 JSON 里加了 both:true 就照发 ✓；没加就默认“语音已经把这轮话说完了” → 不重复 ✗。
                    // 之前用“含不含 5 位数字/8 个字母”猜 ✗ ——那种规则既解释不清也会误伤（“明天 8 点见”就被吞 ✗）。
                    // 唯一保留的自动补发：**链接**（念出来完全没用，这是物理原因，不是猜）。
                    if (result.Both)
                    {
                        _hooks.Log("[Voice] 模型要求语音+文字都发（both）→ 文字照发");
                    }
                    else if (TextRules.CarriesLink(textReply))
                    {
                        _hooks.Log("[Voice] 文字里有链接（语音念不出来）→ 补发文字");
                    }
                    else
                    {
                        _hooks.Log($"[Voice] 语音说了这轮的话{(TextRules.SameSaid(textReply, said) ? "（就是同一句）" : string.Empty)}"
                                + $" → 文字不再重复发（被吞掉 {textReply.Length} 字；想同时发文字需 both:true）");
                        textReply = null;
                    }
                }
            }
            else
            {
                textReply ??= string.Join(" ", voiceParts);
            }

            // 个别段没发出去的：那几段当文字补上
            if (voiceFailed.Count > 0)
            {
                var fallback = string.Join(" ", voiceFailed.Where(p => p.Length > 0));
                if (fallback.Length > 0)
                {
                    textReply = textReply is null ? fallback : textReply + "\n" + fallback;
                }
            }
        }

        if (_riskBackoff?.IsActive(conversation.SourceKey) == true)
        {
            var mentioned = triggerMessageId is long mentionId &&
                conversation.Messages.FirstOrDefault(m => m.QqMessageId == mentionId)?.MentionedBot == true;
            var decision = _riskBackoff.EvaluateText(conversation.SourceKey, mentioned, textReply ?? string.Empty);
            if (!decision.Allowed || decision.Text.Length == 0)
            {
                _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: "protocol_backoff");
                return;
            }
            textReply = decision.Text;
            sticker = null;
            pokeTarget = null;
        }

        var sent = voiceSent;
        var sendDirect = directAddress && triggerMessageId is long mentionTrigger &&
            conversation.Messages.FirstOrDefault(m => m.QqMessageId == mentionTrigger)?.MentionedBot == true;
        // 分句发送的**逐段**结果（issue #14）：前几段成功、后面失败时，
        // 只有真正发出去的段落才写进会话历史 —— 不能因为"整条返回 false"就把已发的当成没发。
        var effectiveReplyTo = platformPolicy.Feature("quote", true).Enabled ? replyTo : null;
        var sendReport = textReply is not null
            ? await _plain.SendWithCadenceAsync(conversation.SourceKey, isGroup, targetId, textReply, effectiveReplyTo,
                _riskBackoff?.IsActive(conversation.SourceKey) == true ? sendDirect : directAddress)
            : CadenceSendReport.None;
        var sentText = sendReport.AnySent;
        if (sentText)
        {
            sent = true;
        }
        _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound,
            textReply is null ? "silent" : sendReport.AllSent ? "sent" : sentText ? "partial" : "blocked",
            reasonCode: sendReport.FailureReasonCode, count: textReply?.Length);

        var sentImage = false;
        if (sticker is not null)
        {
            // 引用只给第一条消息，避免“文字 + 图”两条都带引用
            sentImage = await SendStickerAsync(isGroup, targetId, sticker, textReply is not null ? null : replyTo, conversation.SourceKey);
            sent = sent || sentImage;
            if (sentImage) _stickers.Store.MarkUsed(sticker.Id);
        }

        var pokeSent = pokeTarget is long pokeUserId &&
            await TrySendPokeAsync(conversation, context, caps, platformPolicy, isGroup, targetId, pokeUserId);

        if (sent || pokeSent)
        {
            // 文字那一段：全部发出 → 记原文（与改造前逐字一致）；部分成功 → 只记**实际发出**的段落。
            var recordedTextBody = sentText
                ? sendReport.AllSent ? textReply! : sendReport.Text
                : null;
            var recordedText = voiceSent
                ? recordedTextBody is not null ? $"{recordedTextBody}（同时用语音说：{voiceText}）" : $"[语音] {voiceText}"
                : recordedTextBody is not null ? recordedTextBody
                : sentImage ? "[表情包]"
                : "[戳一戳]";
            var appended = new ChatMessage
            {
                Role = MessageRole.Self,
                Text = recordedText,
                Timestamp = EngineClock.Now
            };
            conversation.Append(appended);
            _registry.Touch(conversation);
            _ui.NotifyMessageAdded(conversation.SourceKey, appended);
            _registry.Save();
        }

        // 引用目标写进日志（handoff-4 §23.3 B：“真验证需要把每次带引用的回复 + 上下文存下来人工看几十条”）。
        // 只写“带引用”的话，事后根本看不出引到了谁头上 —— 复盘只能靠猜。
        // 2026-09-16 加：连**触发那条**也写上 —— “正文回答 A、引用挂到 B”这类错位，
        // 只有把两边摆在一起才看得出来（管理员反馈“引用错误”时就是靠这个定位的）。
        var quoteNote = string.Empty;
        if (triggerMessageId is long loggedTriggerId)
        {
            var trig = context.FirstOrDefault(m => m.QqMessageId == loggedTriggerId);
            if (trig is not null)
            {
                quoteNote += "，触发→" + (trig.SenderName ?? "?") + "「" + TextRules.Shorten(trig.Text ?? string.Empty, 14) + "」";
            }
        }

        if (replyTo is long loggedQuoteId)
        {
            var quoted = context.FirstOrDefault(m => m.QqMessageId == loggedQuoteId);
            quoteNote = quoted is null
                ? "，带引用"
                : "，带引用→" + (quoted.SenderName ?? "?") + "「" + TextRules.Shorten(quoted.Text ?? string.Empty, 18) + "」";
        }

        // P1（只观测）：把这轮的终态喂给参与状态机 —— 发出去了=Replied，没发出去=Silent。
        // 注意：**返回值故意不用**，本轮不改变任何发送/拦截行为。
        _participation.Observe(
            conversation.SourceKey,
            sent
                ? Services.Participation.ParticipationEvent.Replied
                : Services.Participation.ParticipationEvent.Silent);

        // P2（结构化运行记录）：同一条终态也用统一字段写一行，便于核对「决策 → 实际发送」是否一致。
        _hooks.Log($"[决策] outcome={(sent ? "replied" : "failed")} action={result.Action}"
                + $" reason={result.ReasonCode ?? "unknown"}: {conversation.Name}");

        _hooks.Log(
            $"{(sent ? "已回复" : "回复失败")} {conversation.Name}（{elapsed:F0}ms 生成" +
            $"{(result.Suitability is int sc ? $"，自评 {sc}" : string.Empty)}" +
            (reply.Length > 0 ? $"，{reply.Length} 字" : string.Empty) +
            (sentText && !sendReport.AllSent ? $"，部分发出（{sendReport.SentSegments.Count}/{TextRules.SplitSentences(reply).Count} 段）" : string.Empty) +
            (voiceSent ? $"，语音 {voiceText.Length} 字" : string.Empty) +
            (sticker is not null ? $"，表情包 #{sticker.Id}（{StickerText.Describe(sticker)}）" : string.Empty) +
            (pokeSent ? $"，戳了 {pokeTarget}" : string.Empty) +
            $"{quoteNote}）" +
            (reply.Length > 0 ? $": {reply}" : string.Empty));
    }

    /// <summary>戳一戳发送与前置闸门裁决：仅在目标出现于当前上下文且心情/能力闸门放行时发出。</summary>
    private async Task<bool> TrySendPokeAsync(
         BotConversation conversation,
         IReadOnlyList<ChatMessage> context,
         Domain.Permissions.ChatCapabilitySet caps,
         EffectivePlatformPolicy platformPolicy,
        bool isGroup,
        long targetId,
        long pokeUserId)
    {
        var pokeFeature = platformPolicy.Feature("poke", globallyEnabled: true);
        if (!pokeFeature.Enabled)
        {
            _hooks.Log($"这次不戳（平台策略拒绝：{pokeFeature.ReasonCode}）");
            return false;
        }

        var known = context.Any(m => m.SenderId == pokeUserId) ||
                    _poke.IsRecentPoker(conversation.SourceKey, pokeUserId);
        if (!known)
        {
            _hooks.Log($"模型想戳 {pokeUserId}，但这个人没在本次上下文里出现过 → 忽略（防编造号码）");
            return false;
        }
        if (!_mood.WillPokeBack(EngineClock.Now, out var moodWhy))
        {
            _hooks.Log($"这次不戳 {pokeUserId}（{moodWhy}）");
            return false;
        }
        if (!_poke.AllowPokeBack(conversation.SourceKey, pokeUserId, out var pokeWhy))
        {
            _hooks.Log($"这次不戳 {pokeUserId}（{pokeWhy}）");
            return false;
        }
        if (!_approvals.AllowCapability(conversation, "poke.send", null, out var pokeCapWhy, pinned: caps))
        {
            _hooks.Log($"这次不戳 {pokeUserId}（能力闸门拒绝：{pokeCapWhy}）");
            return false;
        }
        var sent = await _source.SendPokeAsync(isGroup, targetId, pokeUserId);
        if (sent) _poke.NotePokedBack(conversation.SourceKey, pokeUserId, EngineClock.Now);
        else _hooks.Log($"戳 {pokeUserId} 失败（协议端可能不支持戳一戳）");
        return sent;
    }

    /// <summary>
    /// 代码侧唯一的语音硬护栏（秒）——**只是防炸**，不是“该不该发语音”的判断。
    /// 2026-09-21 管理员说“判别逻辑不自然”✗：以前这里按「语音积极性」算 15~180 秒的硬门，
    /// 模型兴致上来想用语音，却被代码按回去 ✗，而且它自己看不见这个拦截（只被告知“有硬约束”）。
    /// 现在：**节奏归模型**（提示词把积极性、上次语音的事实都给它），代码只挡同一会话几秒内连发两条。
    /// </summary>
    private const int VoiceBreakerSeconds = 8;
    /// <summary>面板上「语音积极性」对应给模型的**建议节奏**（秒）——只进提示词，不再拦人。</summary>
    /// <summary>
    /// 同一个会话两次发语音的最小间隔（秒）：**跟着面板的「语音积极性」缩放**。
    /// 为什么要跟着动：写死 45 秒时，“积极性拉到 100”其实一点也积极不起来（该发还是被拦）。
    /// 口径与提示词共用（<see cref="VoiceCadence.IntervalSeconds"/>）—— 模型看到的数字与真正拦住它的数字是同一个。
    /// </summary>
    private int VoiceSuggestIntervalSeconds => VoiceCadence.IntervalSeconds(_settings.VoiceEagerness);
    /// <summary>
    /// 语音频率门。为什么要它：
    ///   • 语音在群里是“稀罕事”，几秒内连发就是刷屏（和表情包同一个道理）；
    ///   • 合成+转码是串行的，连发会排队卡住后面的消息。
    /// 只挡“几秒内连发”这种物理性的问题；频率是否得体由模型自己判断（提示词给它积极性与建议节奏）。
    /// </summary>
    private bool AllowVoice(BotConversation conversation, out string reason)
        => _voice.Allow(conversation.SourceKey, out reason);
    /// <summary>表情包频率门（实现见 StickerService.Allow）：同一会话冷却内、同一张 10 分钟内都不发。</summary>
    private bool AllowSticker(BotConversation conversation, string stickerId, out string reason)
        => _stickers.Allow(conversation.SourceKey, stickerId, out reason);
    /// <summary>发一张表情包（实现见 StickerService.SendAsync：读盘 → base64 → image 段，发出去才记账）。</summary>
    private Task<bool> SendStickerAsync(bool isGroup, long targetId, StickerRecord sticker, long? replyTo, string sourceKey)
        => _stickers.SendAsync(isGroup, targetId, sticker, replyTo, sourceKey);
}
