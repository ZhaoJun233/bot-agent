using BotAgent.Services.Qq;
using BotAgent.Services.Stickers;

namespace BotAgent.Services.Ops;

/// <summary>
/// 启动自述（那两行）：进程起来第一眼要看清的东西 —— AI 总开关、私域/官方两份名单与对话总开关、
/// 用的哪个模型（含快速档）、人设配没配、表情包库有多少张。
///
/// 为什么单独一个类、由宿主在**状态就位之后**调一次：它讲的是"这张对象图此刻长什么样"
/// （配置 + 白名单 + 表情包库），而这三样只有装配点同时握着。
/// 以前这段写在 <c>BotAgentHost.Start()</c> 里，于是"打印一句启动日志"成了它必须持有配置与白名单的理由
/// —— 批次 5 那 43 个字段里有两个就是这么来的。
/// </summary>
public sealed class BootReport
{
    private readonly SettingsBox _box;
    private readonly WhitelistGate _whitelist;
    private readonly StickerService _stickers;

    public BootReport(SettingsBox box, WhitelistGate whitelist, StickerService stickers)
    {
        _box = box;
        _whitelist = whitelist;
        _stickers = stickers;
    }

    /// <summary>配置读取入口：指向**当前发布版**（见 <see cref="SettingsBox" />）。</summary>
    private AppSettings _settings => _box.Current;

    /// <summary>名单空时先来一条警告，再写自述。措辞与搬过来之前**逐字一致**。</summary>
    public void Write()
    {
        var gateState = _whitelist.Describe();

        // 白名单严格模式提示：空名单 = 全部忽略，容器里很容易踩
        if (gateState.BothPrivateListsEmpty)
        {
            FileLog.Warn("Agent",
                "群聊与私聊白名单都是空的 → 严格模式下将忽略所有消息。若要接收，请设 " +
                "QQCHAT_WHITELIST_GROUPS / QQCHAT_WHITELIST_PRIVATES（或旧的 QQCHAT_WHITELIST），" +
                "填具体群号/QQ 号，或写 '*'。");
        }

        FileLog.Write("Agent",
            $"已启动。AI={( _settings.AiModeEnabled ? "开" : "关")}, " +
            $"群聊白名单={gateState.Groups}{(gateState.GroupsFromLegacy ? "（用旧的共用名单）" : "")}, " +
            $"私聊白名单={gateState.Privates}{(gateState.PrivatesFromLegacy ? "（用旧的共用名单）" : "")}, " +
            // 官方那条的名单状态也得印：否则“官方通道被拦”时完全看不出到底是名单空了、还是填了不对的号
            $"官方白名单=群{gateState.OfficialGroups}/私聊{gateState.OfficialPrivates}, " +
            // 总开关状态也印：2026-09-21 线上出现过“某条通道的总开关被关掉 → 一条都不回 →
            // 日志里只有一行很容易被淹没的“忽略（…通道的总开关是关的）”，查了半天 ✗。
            $"对话总开关=私域{(_settings.PrivateChatEnabled ? "开" : "关")}/官方{(_settings.OfficialChatEnabled ? "开" : "关")}, " +
            $"模型={_settings.ReplyModel}" +
            (_settings.FastReply && _settings.ReplyModel != _settings.Model
                ? $"（快速档；主模型 {_settings.Model}）"
                : string.Empty) +
            $", 人设={(string.IsNullOrWhiteSpace(_settings.BotPersona) ? "无" : "已配置")}, " +
            $"表情包={(_settings.EnableStickers ? $"开（{_stickers.Store.Count}/{_settings.StickerLibraryMax} 张，已描述 {_stickers.Store.DescribedCount}）" : "关")}");
    }
}
