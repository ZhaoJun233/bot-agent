using BotAgent.Domain.Plugins;
using BotAgent.Services.Poke;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>
/// 预设插件：双向戳一戳互动与自主回戳插件。
/// </summary>
public sealed class PokePresetPlugin : IBotPlugin
{
    private PokeUseCase? _useCase;

    public PokePresetPlugin() { }

    public PokePresetPlugin(PokeUseCase useCase)
    {
        _useCase = useCase;
    }

    public string Id => "preset.feature.poke";
    public string Name => "双向戳一戳交互";
    public string Version => "1.0.0";
    public string Description => "识别群聊与私聊双向戳一戳动作，具备冷却门限判定与自主拟人化回戳逻辑。";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => true;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _useCase ??= context.GetService<PokeUseCase>();
        context.TryBindToggle(PresetToggleBindings.Poke(), out _);
        context.TryContributeSettings(new SettingsSection(
            "sec-poke", "戳一戳", Id,
            new[]
            {
                new SettingsField("EnablePoke", "启用双向戳一戳互动", "boolean",
                    "识别戳一戳动作并按冷却门限自主回戳"),
                new SettingsField("PokeCooldownSeconds", "主动戳人冷却 (秒)", "number",
                    "两次主动戳人之间的最小间隔")
            }), out _);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
