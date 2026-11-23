using System.Collections.Concurrent;

namespace BotAgent.Services.Reply;

internal readonly record struct PendingPluginToolNote(string ToolId, string Text);

/// <summary>
/// 会话级只读插件工具一次性回执笔记（供默认单步模式下后台执行完成后唤醒下一轮模型消费，取走即清）。
/// </summary>
internal sealed class PluginToolNotes
{
    private const int MaxEntries = 256;
    private readonly ConcurrentDictionary<string, PendingPluginToolNote> _notes = new(StringComparer.Ordinal);

    public void Store(string sourceKey, string toolId, string? text)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(toolId) || string.IsNullOrWhiteSpace(text)) return;
        if (_notes.Count >= MaxEntries && !_notes.ContainsKey(sourceKey))
        {
            foreach (var oldKey in _notes.Keys.Take(32))
                _notes.TryRemove(oldKey, out _);
        }
        _notes[sourceKey] = new PendingPluginToolNote(toolId.Trim(), text.Trim());
    }

    public PendingPluginToolNote? TakeNote(string sourceKey)
        => !string.IsNullOrWhiteSpace(sourceKey) && _notes.TryRemove(sourceKey, out var note) ? note : null;

    public void Clear(string sourceKey)
    {
        if (!string.IsNullOrWhiteSpace(sourceKey))
            _notes.TryRemove(sourceKey, out _);
    }
}
