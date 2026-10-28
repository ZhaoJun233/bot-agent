namespace BotAgent.Domain.Plugins;

/// <summary>
/// 探针契约版本化描述符：用于 UI 与核心/插件柔性协商，防范因契约失配导致 UI 崩溃。
/// </summary>
public sealed record CapabilityDescriptor(
    string CapabilityId,
    string DisplayName,
    int ContractVersion,
    int MinSupportedUiVersion,
    string Category,
    bool IsAvailable = true);