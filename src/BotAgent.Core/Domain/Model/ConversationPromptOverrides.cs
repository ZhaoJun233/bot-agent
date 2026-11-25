namespace BotAgent.Domain.Model;

/// <summary>
/// 按单轮会话传入模型客户端的只读提示词覆写（为 null 时全部回落全局/单例设置）。
/// </summary>
public sealed record ConversationPromptOverrides(
    string? PlatformDisplayName = null,
    bool PlatformNeutral = false,
    string? PersonaOverride = null,
    string? PersonaAppend = null,
    int? SuitabilityThreshold = null,
    int? MaxContextMessages = null,
    int? VoiceMaxChars = null,
    int? VoiceEagerness = null,
    bool? FilterActionNarration = null)
{
    public override string ToString() => "ConversationPromptOverrides";
}
