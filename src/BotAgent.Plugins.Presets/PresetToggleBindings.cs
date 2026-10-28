using BotAgent.Services;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>七个预设插件各自的配置字段绑定（集中一处，便于核对与维护）。</summary>
public static class PresetToggleBindings
{
    public static PresetToggleBinding Music() => new(
        "preset.feature.music",
        s => s.EnableMusic,
        (s, v) => s.EnableMusic = v);

    public static PresetToggleBinding Voice() => new(
        "preset.feature.voice",
        s => s.EnableVoice,
        (s, v) => s.EnableVoice = v);

    public static PresetToggleBinding Stickers() => new(
        "preset.feature.stickers",
        s => s.EnableStickers,
        (s, v) => s.EnableStickers = v);

    public static PresetToggleBinding Poke() => new(
        "preset.feature.poke",
        s => s.EnablePoke,
        (s, v) => s.EnablePoke = v);

    public static PresetToggleBinding WebSearch() => new(
        "preset.feature.websearch",
        s => s.EnableWebSearch,
        (s, v) => s.EnableWebSearch = v);

    public static PresetToggleBinding Vibes() => new(
        "preset.feature.vibes",
        s => s.EnableAtmosphereDamping,
        (s, v) => s.EnableAtmosphereDamping = v);

    public static PresetToggleBinding Profiles() => new(
        "preset.feature.profiles",
        s => s.EnableProfileSummary,
        (s, v) => s.EnableProfileSummary = v);
}