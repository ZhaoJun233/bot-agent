using BotAgent.Domain.Platforms;
using BotAgent.Domain.Tools;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;

namespace BotAgent.Services.Agent;

internal static class QqActionAuthorization
{
    public static ToolOutcome? Denial(QqActionSpec spec, EffectivePlatformPolicy? policy,
        Func<string, bool>? pluginAllows, string platform)
    {
        var plugin = spec.Name == "poke" ? PluginGate.PokeCapability : null;
        if (plugin is not null && (pluginAllows is null || !pluginAllows(plugin)))
            return ToolOutcome.Failure("plugin_denied", pluginAllows is null
                ? $"❌ 动作 {spec.Name} 被插件门禁拒绝（门禁未接线）。"
                : $"❌ 动作 {spec.Name} 被插件开关拒绝（对应插件已关闭）。");
        if (policy is null)
            return platform == PlatformId.QqPrivate ? null
                : ToolOutcome.Failure("platform_unavailable", "当前平台策略不可用，本次动作未执行。");
        if (policy.Context.PlatformId != platform)
            return ToolOutcome.Failure("platform_mismatch", platform == PlatformId.QqPrivate
                ? $"❌ 动作 {spec.Name} 仅支持私域 OneBot，不能用于当前平台。"
                : $"❌ 动作 {spec.Name} 与当前平台不匹配。");
        if (!policy.Registered || !policy.Connected || !policy.Enabled || !policy.ChatEnabled)
            return ToolOutcome.Failure("platform_unavailable", $"❌ 动作 {spec.Name} 被平台策略拒绝（平台未注册、未连接或聊天未启用）。");
        var featureName = spec.Name switch { "send" => "text", "poke" or "recall" => spec.Name, _ => null };
        if (featureName is not null && policy.Feature(featureName) is { Enabled: false } feature)
            return ToolOutcome.Failure("capability_denied", $"❌ 动作 {spec.Name} 被平台能力拒绝（{feature.ReasonCode}）。", feature.ReasonCode);
        return policy.ActionAllowlistConfigured && !policy.CanUseAction(spec.Name)
            ? ToolOutcome.Failure("action_not_allowed", $"❌ 动作 {spec.Name} 未被当前平台策略授权。") : null;
    }
}
