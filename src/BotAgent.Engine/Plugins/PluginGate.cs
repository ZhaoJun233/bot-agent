using System.Collections.Frozen;
using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins;

/// <summary>
/// 能力闸门：把稳定能力键映射到官方预设插件 ID，再向 <see cref="IPluginRegistry" /> 查询开关。
///
/// 语义（冻结，供调用方依赖）：
///   · 已知能力键 → 对应预设插件的 <see cref="IPluginRegistry.IsEnabled" /> 结果；
///     插件**未登记**与**已禁用**一样返回 false（fail closed，注册表不认识的插件不可能被开启）。
///   · 未知能力键 → true：本闸门只管映射表里的能力，未登记进表的能力不由插件开关控制
///     （与“未知工具返回 true”同口径，调用方拼错键时不会把核心链路一并关掉）。
///   · 已知工具 ID / 前缀 → 映射到对应能力后走上面的判定；未知工具 ID 返回 true。
///   · 空字符串 / 空白能力键或工具 ID 视为“未指定”，同样返回 true。
///
/// 纯只读查表：无 IO、无静态可变状态。注册表本身是否线程安全由宿主负责；
/// 本类每次判定都实时查询，因此插件在运行期被启用/禁用会立即生效（不缓存开关状态）。
/// </summary>
public sealed class PluginGate
{
    /// <summary>能力键：点歌/听歌。</summary>
    public const string MusicCapability = "music";

    /// <summary>能力键：语音合成。</summary>
    public const string VoiceCapability = "voice";

    /// <summary>能力键：表情包。</summary>
    public const string StickersCapability = "stickers";

    /// <summary>能力键：戳一戳。</summary>
    public const string PokeCapability = "poke";

    /// <summary>能力键：氛围/情绪。</summary>
    public const string VibesCapability = "vibes";

    /// <summary>能力键：人物档案。</summary>
    public const string ProfilesCapability = "profiles";

    /// <summary>能力键：联网搜索/读网页。</summary>
    public const string WebSearchCapability = "websearch";

    /// <summary>能力键 → 预设插件 ID（稳定契约，不随工具改名漂移）。</summary>
    private static readonly FrozenDictionary<string, string> CapabilityPlugins =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MusicCapability] = "preset.feature.music",
            [VoiceCapability] = "preset.feature.voice",
            [StickersCapability] = "preset.feature.stickers",
            [PokeCapability] = "preset.feature.poke",
            [VibesCapability] = "preset.feature.vibes",
            [ProfilesCapability] = "preset.feature.profiles",
            [WebSearchCapability] = "preset.feature.websearch",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>精确工具 ID → 能力键（网页能力只认这两个只读工具）。</summary>
    private static readonly FrozenDictionary<string, string> ExactToolCapabilities =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["web.search"] = WebSearchCapability,
            ["web.read"] = WebSearchCapability,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>工具 ID 前缀（第一个点号之前的部分）→ 能力键。</summary>
    private static readonly FrozenDictionary<string, string> ToolPrefixCapabilities =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["music"] = MusicCapability,
            ["sticker"] = StickersCapability,
            ["voice"] = VoiceCapability,
            ["poke"] = PokeCapability,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly IPluginRegistry _registry;

    /// <summary>创建能力闸门。</summary>
    /// <param name="registry">插件注册表；不能为 null。</param>
    public PluginGate(IPluginRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// 判断一个能力键是否放开。
    /// 已知能力键以其预设插件是否**已登记且启用**为准（未登记视为禁用，fail closed）；
    /// 未知能力键不由插件闸门管理，返回 true。
    /// </summary>
    public bool Allows(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return true;
        }

        return !CapabilityPlugins.TryGetValue(capability, out var pluginId) || _registry.IsEnabled(pluginId);
    }

    /// <summary>
    /// 判断一个工具是否放开。
    /// 映射规则：<c>web.search</c> / <c>web.read</c> → websearch；
    /// <c>music.*</c> → music；<c>sticker.*</c> → stickers；<c>voice.*</c> → voice；<c>poke.*</c> → poke。
    /// 未命中映射的工具（未知工具）返回 true。
    /// </summary>
    public bool AllowsTool(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return true;
        }

        if (ExactToolCapabilities.TryGetValue(toolId, out var capability))
        {
            return Allows(capability);
        }

        var dot = toolId.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && ToolPrefixCapabilities.TryGetValue(toolId[..dot], out capability))
        {
            return Allows(capability);
        }

        return true;
    }

    /// <summary>
    /// 判断一组工具里是否**至少有一个**被放开（短路求值）。
    /// 空集合或 null 返回 false：没有工具就没有“任意一个可用”。
    /// </summary>
    public bool AllowsAnyTool(IEnumerable<string> toolIds)
    {
        if (toolIds is null)
        {
            return false;
        }

        foreach (var toolId in toolIds)
        {
            if (AllowsTool(toolId))
            {
                return true;
            }
        }

        return false;
    }
}
