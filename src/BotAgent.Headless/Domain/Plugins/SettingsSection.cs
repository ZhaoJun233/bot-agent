namespace BotAgent.Domain.Plugins;

/// <summary>
/// 一个可由插件自述的配置字段。
///
/// 为什么要有它：面板过去把 60 个字段写死在 app.js 里，插件被禁用或根本不存在时
/// 那些字段照样显示、照样能改、照样写进配置 —— 面板与真实能力完全脱节。
/// 字段改由插件自己声明，面板只渲染"当前确实存在的功能"对应的字段。
/// </summary>
/// <param name="Key">AppSettings 上的属性名（面板提交时按它回填）。</param>
/// <param name="Label">显示名。</param>
/// <param name="Type">控件类型：text / number / boolean / select / textarea / secret。</param>
/// <param name="Desc">一行说明。</param>
/// <param name="Step">number 类型的步长。</param>
/// <param name="Options">select 类型的候选值。</param>
/// <param name="SecretName">secret 类型的密钥名（写入 secrets 表，不进 settings.json）。</param>
/// <param name="SecretFlag">secret 类型的状态回显键名（如 _secretApiKeyConfigured）。</param>
public sealed record SettingsField(
    string Key,
    string Label,
    string Type,
    string? Desc = null,
    string? Step = null,
    string[]? Options = null,
    string? SecretName = null,
    string? SecretFlag = null);

/// <summary>
/// 插件自述的一组配置：面板按分组渲染，分组的存活与否跟插件启停走。
/// </summary>
/// <param name="SectionId">分组标识（同时用作面板锚点 id）。</param>
/// <param name="Title">分组显示名（不带序号）。</param>
/// <param name="PluginId">贡献者插件 ID：该插件未启用时整组不出现。</param>
/// <param name="Fields">该组的字段清单。</param>
public sealed record SettingsSection(
    string SectionId,
    string Title,
    string PluginId,
    IReadOnlyList<SettingsField> Fields);