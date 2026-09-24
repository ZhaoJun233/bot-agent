using BotAgent.Domain.Ports;
using BotAgent.Services.Agent;
using BotAgent.Services.Qq;
using BotAgent.Services.Panel;
using BotAgent.Services.Reply;
using BotAgent.Services.Stickers;
using BotAgent.Services.OneBot;

namespace BotAgent.Services.Ops;

/// <summary>
/// 后台巡检（宿主自己的那几个定时器 + 两个探测）：静默兜底、画像巡检、QQ 账号在线探测。
///
/// 为什么单拎出来：这些全是"按配置定时做事"，与回复链、与面板都无关；它们的**状态**（上一次用的配置、
/// 正在跑没有、账号在线没在线、画像成功/失败计数）以前散在 BotAgentHost 上，占了一堆字段。
///
/// 三条纪律：
///   ① 定时器只在**配置真的变了**时重建（<see cref="RebuildIfNeeded" /> 里逐项比对，避免每次保存都重置计时）；
///   ② 兜底/巡检都**不许并发叠着跑**（<c>Interlocked.Exchange</c> 占位，跑不完就跳过这一轮）；
///   ③ 宿主已经在收摊（<c>IsDisposed</c>）时一律不启动新的一轮。
/// </summary>
public sealed class BotScheduler
{
    private readonly SettingsBox _box;
    private readonly IQqChatSource _source;
    private readonly IProfileRepository _profiles;
    private readonly IModelClient _brain;
    private readonly ReplyPipeline _reply;
    private readonly StickerService _stickers;
    private readonly PanelNotifier _ui;
    private readonly BotSchedulerHooks _hooks;

    private Timer? _idleTimer;
    private Timer? _summaryTimer;
    private Timer? _healthTimer;
    private int _summaryRunning;
    private int _healthRunning;
    private int _accountOnline = -1;

    private int _timerIdleSeconds = -1;
    private bool _timerSummaryEnabled;
    private int _timerSummarySeconds = -1;
    private bool _timerStickersEnabled;

    private readonly SemaphoreSlim _summaryGate = new(1, 1);
    private long _summaryDoneCount;
    private long _summaryFailCount;

    public BotScheduler(
        SettingsBox box,
        IQqChatSource source,
        IProfileRepository profiles,
        OpenAiClient brain,
        ReplyPipeline reply,
        StickerService stickers,
        PanelNotifier ui,
        BotSchedulerHooks hooks)
    {
        _box = box;
        _source = source;
        _profiles = profiles;
        _brain = brain;
        _reply = reply;
        _stickers = stickers;
        _ui = ui;
        _hooks = hooks;
    }

    private AppSettings _settings => _box.Current;

    /// <summary>QQ 账号在线状态：null = 还不知道（协议端没答复过）。</summary>
    public bool? AccountOnline
        => Volatile.Read(ref _accountOnline) is var v && v >= 0 ? v == 1 : null;

    /// <summary>画像巡检：成功 / 失败累计（面板与健康日报要报）。</summary>
    public long SummaryDoneCount => Interlocked.Read(ref _summaryDoneCount);

    public long SummaryFailCount => Interlocked.Read(ref _summaryFailCount);

    /// <summary>连接断了 → 账号在线状态回到未知（重连后再探一次才知道）。</summary>
    public void ResetAccountState() => Interlocked.Exchange(ref _accountOnline, -1);

    /// <summary>启动：建定时器 + 起账号在线探测（连接建立后会立即先探一次）。</summary>
    public void Start()
    {
        RebuildIfNeeded();

        // QQ 账号在线探测：30 秒一轮（连接建立后会立即先探一次）
        _healthTimer = new Timer(
            _ => _ = CheckAccountOnlineAsync(), null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30));
    }

    /// <summary>收摊：停掉所有定时器（在途那一轮自然结束）。</summary>
    public void Stop()
    {
        _idleTimer?.Dispose();
        _summaryTimer?.Dispose();
        _healthTimer?.Dispose();
        _idleTimer = null;
        _summaryTimer = null;
        _healthTimer = null;
    }

    /// <summary>设置变了 → 只在**相关项真的变了**时才重建定时器（否则每次保存都会重置计时）。</summary>
    public void RebuildIfNeeded()
    {
        var idleSeconds = Math.Max(0, _settings.IdleFallbackSeconds);
        var summaryEnabled = _settings.EnableProfileSummary;
        var summarySeconds = Math.Max(0, _settings.ProfileSummaryIntervalSeconds);
        var stickersEnabled = _settings.EnableStickers && _settings.StickerLibraryMax > 0;

        if (idleSeconds == _timerIdleSeconds &&
            summaryEnabled == _timerSummaryEnabled &&
            summarySeconds == _timerSummarySeconds &&
            stickersEnabled == _timerStickersEnabled)
        {
            return;
        }

        _timerIdleSeconds = idleSeconds;
        _timerSummaryEnabled = summaryEnabled;
        _timerSummarySeconds = summarySeconds;
        _timerStickersEnabled = stickersEnabled;

        // ---- 静默兜底 ----
        _idleTimer?.Dispose();
        _idleTimer = null;
        if (idleSeconds > 0 && !_hooks.IsDisposed())
        {
            _idleTimer = new Timer(
                _ => _reply.IdleFallbackTick(),
                null,
                TimeSpan.FromSeconds(idleSeconds),
                TimeSpan.FromSeconds(idleSeconds));
        }

        // ---- 长期记忆：画像巡检 ----
        _summaryTimer?.Dispose();
        _summaryTimer = null;
        if (summaryEnabled && summarySeconds > 0 && !_hooks.IsDisposed())
        {
            _summaryTimer = new Timer(
                _ => _ = RunProfileSummarizationAsync(),
                null,
                // 首次很快跑一轮（刚打开就能看到效果），之后按配置间隔
                TimeSpan.FromSeconds(Math.Min(5, summarySeconds)),
                TimeSpan.FromSeconds(summarySeconds));
        }

        // ---- 表情包：补说明 + 自巡检（定时器归 StickerService 自己管）----
        _stickers.EnsureTimer(stickersEnabled);

        _hooks.Log($"定时器已按新配置重建（静默兜底 {idleSeconds}s，画像巡检 {(summaryEnabled && summarySeconds > 0 ? summarySeconds + "s" : "关")}，" +
                   $"表情包巡检 {(stickersEnabled && _settings.StickerCurateIntervalSeconds > 0 ? _settings.StickerCurateIntervalSeconds + "s" : "关")}）");
    }

    /// <summary>画像巡检：把够条件的成员交给模型写一句画像（单轮不并发，失败只记数）。</summary>
    private async Task RunProfileSummarizationAsync()
    {
        if (!_settings.EnableProfileSummary || _hooks.IsDisposed())
        {
            return;
        }

        if (Interlocked.Exchange(ref _summaryRunning, 1) == 1)
        {
            return; // 上一轮还没跑完
        }

        try
        {
            var candidates = _profiles.FindSummarizable(_settings.ProfileSummaryThreshold, maxCandidates: 20);

            foreach (var c in candidates)
            {
                if (_hooks.IsDisposed())
                {
                    break;
                }

                await _summaryGate.WaitAsync();
                try
                {
                    var text = await _brain.SummarizePersonaAsync(
                        string.IsNullOrWhiteSpace(c.Name) ? $"QQ{c.Uid}" : c.Name,
                        c.ExistingSummary,
                        c.NewMessages,
                        _settings.ProfileSummaryMaxChars);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        Interlocked.Increment(ref _summaryFailCount);
                        continue;
                    }

                    _profiles.ApplySummary(c.Uid, c.Scope, text, c.ThroughSeq, c.FoldedCount);
                    Interlocked.Increment(ref _summaryDoneCount);
                    _hooks.Log($"画像已生成：{c.Name}({c.Uid}) @ {c.Scope}（{c.NewMessages.Count} 条 → {text.Length} 字）");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _summaryFailCount);
                    FileLog.Write("Profiles", $"画像摘要失败 {c.Uid}@{c.Scope}: {ex.Message}");
                }
                finally
                {
                    _summaryGate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write("Profiles", "画像巡检异常: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _summaryRunning, 0);
        }
    }

    /// <summary>
    /// QQ 账号在线探测（协议端 <c>get_login_info</c> 那类动作）：掉线要**只报一次**，恢复也报一次。
    /// 协议端没明确答复时不误报（保持原状态）；探测失败不打扰用户，下一轮自然重试。
    /// </summary>
    public async Task CheckAccountOnlineAsync()
    {
        if (_hooks.IsDisposed() || _source is not OneBotGateway gateway || !gateway.IsConnected)
        {
            return;
        }

        if (Interlocked.Exchange(ref _healthRunning, 1) == 1)
        {
            return; // 上一轮还没回来
        }

        try
        {
            var online = await gateway.GetAccountOnlineAsync();
            if (online is null)
            {
                return; // 协议端没明确答复 → 不误报，保持原状态
            }

            var next = online.Value ? 1 : 0;
            var previous = Interlocked.Exchange(ref _accountOnline, next);
            if (previous == next)
            {
                return; // 状态未变，不刷日志
            }

            if (next == 0)
            {
                _hooks.Log("⚠ QQ 账号已离线（登录失效或被顶号）：从现在起收不到任何消息。" +
                           "请打开 NapCat 管理面板重新扫码登录（面板地址见部署目录 README）。");
            }
            else
            {
                _hooks.Log("QQ 账号已上线，恢复正常接收消息。");
            }

            _ui.NotifyStateChanged();
        }
        catch
        {
            // 探测失败不打扰用户：下一轮自然会重试
        }
        finally
        {
            Interlocked.Exchange(ref _healthRunning, 0);
        }
    }
}

/// <summary>
/// 后台巡检要用到的宿主能力（由 BotAgentHost 提供实现）。
/// </summary>
/// <param name="Log">普通运行日志（写文件 + 推面板）。</param>
/// <param name="IsDisposed">宿主是否已经在收摊（收摊中不再启动新的一轮）。</param>
public readonly record struct BotSchedulerHooks(
    Action<string> Log,
    Func<bool> IsDisposed);
