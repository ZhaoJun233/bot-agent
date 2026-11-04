using System.Collections.Generic;

namespace BotAgent.Domain.Tools;

/// <summary>
/// 一次判定时**可用开关的取值快照**（纯数据，不读设置、不读时钟）。
///
/// 之所以单独一个类型：聊天那一路有两种“把开关算成可见性”的时机 ——
/// ① 保存/回读时算白名单（<c>ChatCapabilitySet.FromSwitches</c>，按策略版本固化）；
/// ② 每一轮真正调用前现算（<c>ReplyToolAvailability</c> / <c>ToolVisibility</c>，防热重载空窗）。
/// 两处口径必须逐字一致，于是它们共用这里的 <see cref="ToolSwitches.Enabled" />，
/// 不再各写一张 id→开关 的表（#115：那张表原来抄了两份，改一个键要改两处）。
/// </summary>
public readonly record struct ToolSwitchState(
    bool WebSearch,
    bool Music,
    bool Voice,
    bool Stickers,
    bool Poke,
    bool Approvals,
    bool Questions)
{
    /// <summary>从设置对象的常用开关取值构造（调用方决定 webReady 这类运行时门控）。</summary>
    public static ToolSwitchState From(bool webSearch, bool music, bool voice, bool stickers, bool poke,
        bool approvals, bool questions) => new(webSearch, music, voice, stickers, poke, approvals, questions);
}

/// <summary>
/// **唯一的**“开关名 → 是否可用”判定（#115）。<see cref="ToolSpec.FeatureKey" /> 是输入，
/// 不认识的名字 / 空名字一律返回 false（Fail-Closed；空名字只给“默认行为”类工具用，
/// 它们不经过这里，由调用方直接放行）。
/// </summary>
public static class ToolSwitches
{
    /// <summary>按声明的开关名取值；未知名字 = 关。</summary>
    public static bool Enabled(ToolSpec spec, ToolSwitchState state) => spec.FeatureKey switch
    {
        ToolFeature.WebSearch => state.WebSearch,
        ToolFeature.Music => state.Music,
        ToolFeature.Voice => state.Voice,
        ToolFeature.Stickers => state.Stickers,
        ToolFeature.Poke => state.Poke,
        ToolFeature.Approval => state.Approvals,
        ToolFeature.Question => state.Questions,
        ToolFeature.Text => true,
        _ => false,
    };

    /// <summary>按开关名取值（没有 <see cref="ToolSpec" /> 时用；未知名字 = 关）。</summary>
    public static bool Enabled(string featureKey, ToolSwitchState state) => featureKey switch
    {
        ToolFeature.WebSearch => state.WebSearch,
        ToolFeature.Music => state.Music,
        ToolFeature.Voice => state.Voice,
        ToolFeature.Stickers => state.Stickers,
        ToolFeature.Poke => state.Poke,
        ToolFeature.Approval => state.Approvals,
        ToolFeature.Question => state.Questions,
        ToolFeature.Text => true,
        _ => false,
    };
}
