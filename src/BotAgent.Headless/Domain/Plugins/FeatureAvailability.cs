using BotAgent.Services;

namespace BotAgent.Domain.Plugins;

/// <summary>
/// 功能键常量：与插件 ID 一一对应，也用于能力探针与面板分组归属。
///
/// 为什么要有它：面板、核心、插件三方都要用同一个字符串指代"表情包"这件事，
/// 散落的字面量一旦拼错，表现就是"功能静默消失"——最难查的一类故障。
/// </summary>
public static class FeatureKeys
{
    public const string Stickers = "stickers";
    public const string Voice = "voice";
    public const string Music = "music";
    public const string WebSearch = "websearch";
    public const string LinkPreview = "linkpreview";
    public const string Vibes = "vibes";
    public const string Poke = "poke";
    public const string Profiles = "profiles";
}

/// <summary>
/// 功能可用性判定：**功能是否真的存在**，而不是"面板要不要显示它"。
///
/// 判定口径（两条件同时满足才算存在）：
///   ① 供给该功能的插件程序集已加载；
///   ② 该功能对应的配置开关为 true（插件启停正是通过写这个开关落地的）。
/// 任一不满足 → 功能实例以"未启用"状态构造，其全部对外方法直接短路。
/// 这样"插件禁用/不存在"与"功能不存在"才是同一件事。
/// </summary>
public sealed class FeatureAvailability
{
    private readonly Func<bool> _presetsLoaded;
    private readonly Func<AppSettings> _settings;

    public FeatureAvailability(Func<bool> presetsLoaded, Func<AppSettings> settings)
    {
        _presetsLoaded = presetsLoaded;
        _settings = settings;
    }

    /// <summary>供给功能的插件程序集是否已加载（程序集缺失时全部功能都不可用）。</summary>
    public bool PluginLoaded => _presetsLoaded();

    public bool IsAvailable(string featureKey)
    {
        if (!_presetsLoaded())
        {
            return false;
        }

        var s = _settings();
        return featureKey switch
        {
            FeatureKeys.Stickers => s.EnableStickers,
            FeatureKeys.Voice => s.EnableVoice,
            FeatureKeys.Music => s.EnableMusic,
            FeatureKeys.WebSearch => s.EnableWebSearch,
            FeatureKeys.LinkPreview => s.EnableLinkPreview,
            FeatureKeys.Vibes => s.EnableAtmosphereDamping,
            FeatureKeys.Poke => s.EnablePoke,
            FeatureKeys.Profiles => s.EnableProfileSummary,
            _ => false
        };
    }
}