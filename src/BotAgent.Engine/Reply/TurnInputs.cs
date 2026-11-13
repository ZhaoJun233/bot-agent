using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Stickers;

namespace BotAgent.Services.Reply;

/// <summary>
/// 一轮回复的"输入"（第 1 步的产物）：上下文 + 提示词素材。只读、不可变，后面几步都拿它取数。
/// 最新对方消息的编号在取上下文时固定，避免消息撤回改变生成后的引用兜底判断。
/// </summary>
public sealed record TurnInputs(
    DateTime Started,
    AppSettings Snapshot,
    ChatCapabilitySet Caps,
    EffectivePlatformPolicy PlatformPolicy,
    IReadOnlyList<ChatMessage> Context,
    List<string> Profiles,
    int ProfileChars,
    List<StickerChoice> StickerChoices,
    int RoleCount,
    string? GroupRoles,
    string? VibeHint,
    string PreviousVibe,
    bool PokeContext,
    string? MoodText,
    string? MusicText,
    string? RecallText,
    string? SearchText,
    string? LinkText,
    bool IsPrivateConversation = false,
    long? LatestPeerMessageIdAtGeneration = null);
