namespace BotAgent.Domain.Plugins;

/// <summary>
/// 一次插件启停操作的结果。
/// Persisted = 是否真正落到了配置字段（重启后仍有效）；
/// Rejected = 服务端硬拒绝（例如管理类插件不允许禁用）。
/// </summary>
public sealed record PluginToggleResult(
    bool Ok,
    bool Persisted,
    bool Rejected,
    string? Error)
{
    public static PluginToggleResult PersistedOk { get; } = new(true, true, false, null);
    public static PluginToggleResult MemoryOnly { get; } = new(true, false, false, null);
    public static PluginToggleResult Reject(string reason) => new(false, false, true, reason);
    public static PluginToggleResult Fail(string reason) => new(false, false, false, reason);
}