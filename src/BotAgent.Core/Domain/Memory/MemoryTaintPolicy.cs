namespace BotAgent.Domain.Memory;

/// <summary>Highest sensitivity tier of memory fragments consumed during an execution scope.</summary>
public enum MemorySensitivityTier
{
    None = 0,
    StylePreference = 1,
    PersonalFact = 2,
    LegacyProfile = 3
}

/// <summary>Outward data exposure surface of a tool, media presentation, or state mutation.</summary>
public enum ToolEgressClass
{
    LocalPresentation = 0,
    SameScopeRead = 1,
    ExternalReadEgress = 2,
    ExternalSideEffect = 3,
    GlobalStateWrite = 4
}

/// <summary>ADR-0005 fail-closed matrix governing memory sensitivity and tool egress coexistence.</summary>
public static class MemoryTaintPolicy
{
    public static MemorySensitivityTier TierOf(UnifiedMemoryFactCategory category) => category switch
    {
        UnifiedMemoryFactCategory.ReplyLanguage
            or UnifiedMemoryFactCategory.ReplyLength
            or UnifiedMemoryFactCategory.ExplanationStyle => MemorySensitivityTier.StylePreference,
        (UnifiedMemoryFactCategory)3 => MemorySensitivityTier.StylePreference, // Timezone
        (UnifiedMemoryFactCategory)4 => MemorySensitivityTier.PersonalFact,    // PreferredAddress
        (UnifiedMemoryFactCategory)5 => MemorySensitivityTier.PersonalFact,    // PersonalNote
        _ => MemorySensitivityTier.LegacyProfile
    };

    public static bool Allows(MemorySensitivityTier maxTier, ToolEgressClass egress) => maxTier switch
    {
        MemorySensitivityTier.None => true,
        MemorySensitivityTier.StylePreference => egress is ToolEgressClass.LocalPresentation
            or ToolEgressClass.SameScopeRead
            or ToolEgressClass.ExternalReadEgress,
        MemorySensitivityTier.PersonalFact => egress is ToolEgressClass.LocalPresentation
            or ToolEgressClass.SameScopeRead,
        MemorySensitivityTier.LegacyProfile => false,
        _ => false
    };

    public static ToolEgressClass ClassifyTool(string? toolId, bool readOnly, string? origin = null)
    {
        if (string.IsNullOrWhiteSpace(toolId))
            return ToolEgressClass.ExternalSideEffect;
        if (string.Equals(origin, "Mcp", StringComparison.OrdinalIgnoreCase))
            return readOnly ? ToolEgressClass.ExternalReadEgress : ToolEgressClass.ExternalSideEffect;
        return toolId switch
        {
            "chat.reply" or "voice.speak" or "sticker.send" or "poke.send" => ToolEgressClass.LocalPresentation,
            "doc.read" or "chat.digest" or "kb.search" or "kb.list" or "reminder.list" or "todo.list" when readOnly => ToolEgressClass.SameScopeRead,
            "kb.save" or "kb.forget" or "reminder.create" or "reminder.pause" or "reminder.delete"
                or "todo.add" or "todo.complete" or "todo.delete" => ToolEgressClass.GlobalStateWrite,
            "web.search" or "web.read" or "music.listen" when readOnly => ToolEgressClass.ExternalReadEgress,
            "music.share" => ToolEgressClass.ExternalSideEffect,
            _ => readOnly ? ToolEgressClass.ExternalReadEgress : ToolEgressClass.ExternalSideEffect
        };
    }
}
