namespace BotAgent.Services.Plugins;

public sealed record PluginUiSessionLease(
    string SessionId,
    string PluginId,
    string GenerationId,
    string ReleaseSha256,
    bool Active);

public sealed partial class PluginManager
{
    private const int MaxUiSessions = 512;
    private long _uiSessionOrder;
    private readonly Dictionary<string, (Entry? Generation, string PluginId, string ReleaseSha256, string GenerationId, long Order)> _uiSessions
        = new(StringComparer.Ordinal);

    public string GetGenerationId(string pluginId)
    {
        lock (_sync)
            return _plugins.TryGetValue(pluginId, out var entry) ? entry.GenerationId : "unloaded";
    }

    public PluginUiSessionLease OpenUiSession(string pluginId, string releaseSha256)
    {
        lock (_sync)
        {
            ThrowIfStopped();
            if (_uiSessions.Count >= MaxUiSessions)
            {
                var overflow = _uiSessions.Count - MaxUiSessions + 1;
                foreach (var oldest in _uiSessions.OrderBy(p => p.Value.Order).Take(overflow).Select(p => p.Key).ToArray())
                    _uiSessions.Remove(oldest);
            }
            _plugins.TryGetValue(pluginId, out var entry);
            var sessionId = "uis-" + Guid.NewGuid().ToString("N")[..16];
            var generationId = entry?.GenerationId ?? "unloaded";
            _uiSessions[sessionId] = (entry, pluginId, releaseSha256, generationId, ++_uiSessionOrder);
            return new(sessionId, pluginId, generationId, releaseSha256, entry is not null && !entry.Retired);
        }
    }

    public bool IsUiSessionCurrent(string sessionId, string pluginId, string releaseSha256)
    {
        lock (_sync)
        {
            if (!_uiSessions.TryGetValue(sessionId, out var session)) return false;
            if (session.PluginId != pluginId || !session.ReleaseSha256.Equals(releaseSha256, StringComparison.OrdinalIgnoreCase))
                return false;
            return session.Generation is not null
                && !session.Generation.Retired
                && _plugins.TryGetValue(pluginId, out var current)
                && ReferenceEquals(current, session.Generation);
        }
    }

    /// <summary>Returns true while the UI session has not been closed or invalidated by retirement.</summary>
    public bool HasOpenUiSession(string sessionId, string pluginId, string releaseSha256)
    {
        lock (_sync)
        {
            if (_stopped || !_uiSessions.TryGetValue(sessionId, out var session)) return false;
            if (session.PluginId != pluginId || !session.ReleaseSha256.Equals(releaseSha256, StringComparison.OrdinalIgnoreCase))
                return false;
            return session.Generation is null || !session.Generation.Retired;
        }
    }

    public bool CloseUiSession(string sessionId, string pluginId)
    {
        lock (_sync)
        {
            if (!_uiSessions.TryGetValue(sessionId, out var session) || session.PluginId != pluginId)
                return false;
            return _uiSessions.Remove(sessionId);
        }
    }

    public int ActiveUiSessionCount(string pluginId)
    {
        lock (_sync)
            return _uiSessions.Values.Count(session => session.PluginId == pluginId
                && session.Generation is not null && !session.Generation.Retired);
    }

    private void InvalidateUiSessions(Entry retired)
    {
        lock (_sync)
        {
            var stale = _uiSessions.Where(pair => ReferenceEquals(pair.Value.Generation, retired)
                || (pair.Value.Generation is null && pair.Value.PluginId == retired.Info.Id))
                .Select(pair => pair.Key).ToArray();
            foreach (var key in stale) _uiSessions.Remove(key);
        }
    }
}
