using System.Collections.Immutable;

namespace BotAgent.Domain.Configuration;

/// <summary>会话级工具权限最高档位（Phase 1 全部绑定到真实执行闸门）。</summary>
public enum ConversationToolTier
{
    Inherit = 0,
    ChatOnly = 1,
    ReadOnlyTools = 2,
    ManagedTools = 3,
}

/// <summary>会话档案主键：平台驱动身份 + 精确会话键（禁止把 static class PlatformId/AccountScope 作为字段类型）。</summary>
public sealed record ConversationProfileKey(
    DriverKey Driver,
    string ConversationKey)
{
    public override string ToString() => "ConversationProfileKey";
}

/// <summary>轴 1a：表达风格覆写（与 ConversationPromptOverrides 及运行侧严格对称）。</summary>
public sealed record ConversationStyleOverride(
    string? PersonaOverride = null,
    string? PersonaAppend = null,
    bool? FilterActionNarration = null,
    int? VoiceMaxChars = null,
    int? VoiceEagerness = null)
{
    public override string ToString() => "ConversationStyleOverride";
}

/// <summary>轴 1b：回复节奏、适宜度、上下文窗口与主动发言覆写。</summary>
public sealed record ConversationPacingOverride(
    int? GroupInterjectionProbability = null,
    int? GroupInterjectionCooldownSeconds = null,
    int? GroupContinuationWaitSeconds = null,
    double? PrivateContinuationWaitSeconds = null,
    int? SuitabilityThreshold = null,
    int? MaxContextMessages = null,
    ToggleRule ProactiveEnabled = ToggleRule.Inherit,
    int? ProactiveQuietSeconds = null)
{
    public override string ToString() => "ConversationPacingOverride";
}

/// <summary>轴 2 + 轴 3：功能开关覆写与工具最高档位。</summary>
public sealed record ConversationCapabilityOverride(
    ImmutableArray<FeatureOverride> Features = default,
    ConversationToolTier ToolTier = ConversationToolTier.Inherit)
{
    public ImmutableArray<FeatureOverride> Features { get; init; } =
        Features.IsDefault ? ImmutableArray<FeatureOverride>.Empty : Features;

    public override string ToString() => "ConversationCapabilityOverride";
}

/// <summary>单个会话的可组合档案（三轴正交）。</summary>
public sealed record ConversationProfile(
    ConversationProfileKey Key,
    string DisplayLabel,
    ConversationStyleOverride? Style = null,
    ConversationPacingOverride? Pacing = null,
    ConversationCapabilityOverride? Capabilities = null)
{
    public override string ToString() => "ConversationProfile";
}

public enum ConversationProfileEditOperation
{
    Upsert = 0,
    Delete = 1,
}

/// <summary>按会话主键逐行增改/删除的事务编辑项（不覆盖其它会话）。</summary>
public sealed record ConversationProfileEdit(
    ConversationProfileKey Key,
    ConversationProfileEditOperation Operation,
    ConversationProfile? Profile = null)
{
    public override string ToString() => "ConversationProfileEdit";
}

/// <summary>会话档案只读投影（Overview 隐藏真名与风格明文，Editable 返回完整编辑态）。</summary>
public sealed record ConversationProfileView(
    ConversationProfileKey Key,
    string DisplayLabel,
    string ConversationKeyDisplay,
    FieldVisibility Visibility,
    string? DisplayLabelRaw = null,
    ConversationStyleOverride? Style = null,
    ConversationPacingOverride? Pacing = null,
    ConversationCapabilityOverride? Capabilities = null)
{
    public override string ToString() => "ConversationProfileView";
}
