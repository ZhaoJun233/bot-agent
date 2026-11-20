using BotAgent.Engine.Runtime;
using BotAgent.Services.Plugins;

namespace BotAgent.Services.Conversations;

/// <summary>
/// 每个会话最近一次"读到的气氛"宿主薄门面：运行期全部由 <c>preset.feature.vibes</c> 独立包经
/// <see cref="IVibesContribution" /> 贡献；缺包或禁用时返回中性/空值，宿主不保留第二份实现。
/// </summary>
public sealed class VibeTracker
{
    public const string ProviderId = "preset.feature.vibes";

    private readonly PluginManager? _plugins;
    private readonly IVibesContribution? _direct;

    public VibeTracker(PluginManager? plugins = null, IVibesContribution? direct = null)
    {
        _plugins = plugins;
        _direct = direct;
    }

    private static bool IsUnavailable(InvalidOperationException ex)
        => ex.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing";

    private IVibesContribution? CurrentContribution
        => _direct ?? (_plugins is not null && _plugins.TryGetContribution<IVibesContribution>(ProviderId, out var contribution)
            ? contribution : null);

    public void Remember(string sourceKey, string? vibe, string? note)
    {
        try { CurrentContribution?.Remember(sourceKey, vibe, note, EngineClock.Now); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    public string Current(string sourceKey)
    {
        try { return CurrentContribution?.Current(sourceKey, EngineClock.Now) ?? string.Empty; }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return string.Empty; }
    }

    public string? Hint(string sourceKey)
    {
        try { return CurrentContribution?.Hint(sourceKey, EngineClock.Now); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return null; }
    }

    public bool IsSober(string sourceKey)
    {
        try { return CurrentContribution?.IsSober(sourceKey, EngineClock.Now) == true; }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return false; }
    }

    public void ClearConversation(string sourceKey)
    {
        try { CurrentContribution?.ClearConversation(sourceKey); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }
}
