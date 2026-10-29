using System.Text;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Domain.Plugins;

namespace BotAgent.Panel;

internal static partial class ZbaRules
{
    private const int MaxSecretBytes = 8192;
    private const int MaxCredentialKeyLength = 128;

    /// <summary>固定槽位数量上限。</summary>
    internal const int MaxSecretSlots = 6;

    /// <summary>单次请求里已登记凭据键的目标数量上限（独立于固定槽位）。</summary>
    internal const int MaxCredentialKeyTargets = 64;

    internal static void ValidateKey(ConfigurationOperationKey key)
    {
        if (key.Generation.Value == Guid.Empty || key.IssuedUnixSeconds <= 0 || key.Nonce == Guid.Empty)
            throw new ZbaBadRequestException("invalid_operation_key");
    }

    internal static void ValidateKey(ManagedOperationKey key)
    {
        if (key.Generation.Value == Guid.Empty || key.IssuedUnixSeconds <= 0 || key.Nonce == Guid.Empty)
            throw new ZbaBadRequestException("invalid_operation_key");
    }

    /// <summary>
    /// 新 Web 只允许访问工作台（<c>panel:</c>）会话：群聊会话名可能由模型按聊天内容自动综结，
    /// 不通过 ZBA 的会话发现暴露。格式错误 400，来源越界 403。
    /// </summary>
    internal static void ValidatePanelSourceKey(string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > 256 || sourceKey.Any(char.IsControl))
            throw new ZbaBadRequestException("invalid_source_key");
        if (!sourceKey.StartsWith("panel:", StringComparison.Ordinal))
            throw new ZbaBackendException(403, "source_key_not_allowed", "会话来源不在允许范围内。");
    }

    internal static void ValidateIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 128
            || !key.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or '~'))
        {
            throw new ZbaBadRequestException("invalid_idempotency_key");
        }
    }

    /// <summary>
    /// 解析一条秘密编辑的目标标识。固定槽位与已登记凭据键是两套独立目标命名空间，
    /// 用前缀区分，避免“槽位 0”和“凭据键 0”被误判成同一目标。
    /// </summary>
    internal static bool TryResolveSecretTarget(ZbaSecretEdit secret, out string target)
    {
        target = string.Empty;
        var hasSlot = secret.Slot is not null;
        var hasKey = secret.CredentialKey is not null;
        // 恰好一个：两个都给或都不给都不接受。
        if (hasSlot == hasKey) return false;
        if (secret.Slot is { } slot)
        {
            if (!Enum.IsDefined(slot)) return false;
            target = "s:" + ((int)slot).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        var key = secret.CredentialKey!;
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxCredentialKeyLength
            || key.Trim() != key || key.Contains('\0')) return false;
        target = "k:" + key;
        return true;
    }

    /// <summary>
    /// 按目标命名空间分别统计不同目标数。固定槽位受 <see cref="MaxSecretSlots" /> 约束，
    /// 凭据键另有自己的上限，二者互不挤占。
    /// </summary>
    internal static (int Slots, int CredentialKeys) CountDistinctSecretTargets(IReadOnlyList<ZbaSecretEdit> secrets)
    {
        var slots = new HashSet<WritableSecretSlot>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var secret in secrets)
        {
            if (secret is null || !TryResolveSecretTarget(secret, out _)) continue;
            if (secret.Slot is { } slot) slots.Add(slot);
            else keys.Add(secret.CredentialKey!);
        }
        return (slots.Count, keys.Count);
    }

    /// <summary>
    /// 插件 id 同时用于包目录与路径段：小写字母开头，只允许小写字母、数字、点与连字符。
    /// 预置插件（<c>preset.*</c>）也走这条路径（它们只有空设置），因此这里不排除该前缀。
    /// </summary>
    internal static void ValidatePluginId(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > 80
            || !PluginIdPattern().IsMatch(pluginId) || pluginId.Contains("..", StringComparison.Ordinal)
            || pluginId.EndsWith('.'))
        {
            throw new ZbaBadRequestException("invalid_plugin_id");
        }
    }

    /// <summary>
    /// 插件的幂等键由前端生成（<c>plugin:install:id@version:issued:nonce</c>），含冒号与 <c>@</c>，
    /// 所以不能复用任务提交那条更严的 <see cref="ValidateIdempotencyKey" />。
    /// 这里只要求它是可安全落盘的标识：有界、无控制字符、不含路径分隔符。
    /// </summary>
    internal static void ValidatePluginIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 256
            || key.Any(character => character is < ' ' or > '~' || character is '/' or '\\'))
        {
            throw new ZbaBadRequestException("invalid_idempotency_key");
        }
    }

    /// <summary>配置版本是服务端签发的不透明串：只校验边界，不做格式推断。</summary>
    internal static void ValidatePluginRevision(string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.Length > 128
            || revision.Any(character => character is < ' ' or > '~'))
        {
            throw new ZbaBadRequestException("invalid_expected_revision");
        }
    }

    /// <summary>
    /// 清单的**形状**校验（面板侧）：id/版本/SHA-256/下载地址的格式。语义校验（宿主兼容性、
    /// 依赖、设置声明）由宿主侧 <c>PluginPackageValidation</c> 再做一遍，面板不重复实现。
    /// </summary>
    internal static void ValidatePluginManifest(PluginPackageManifest manifest)
    {
        if (manifest is null) throw new ZbaBadRequestException("invalid_manifest");
        ValidatePluginId(manifest.Id);
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 64
            || !FixedVersionPattern().IsMatch(manifest.Version))
            throw new ZbaBadRequestException("invalid_manifest");
        if (string.IsNullOrWhiteSpace(manifest.Sha256) || !Sha256Pattern().IsMatch(manifest.Sha256))
            throw new ZbaBadRequestException("invalid_manifest");
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) || manifest.EntryAssembly.Length > 240
            || string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 512)
            throw new ZbaBadRequestException("invalid_manifest");
        // 下载地址同样只认公开 HTTPS；真正的出站策略在宿主侧逐跳校验。
        if (string.IsNullOrWhiteSpace(manifest.DownloadUrl) || manifest.DownloadUrl.Length > 2048
            || !Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo)
            || !string.IsNullOrEmpty(url.Fragment))
            throw new ZbaBadRequestException("invalid_manifest");
    }

    [System.Text.RegularExpressions.GeneratedRegex("^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$")]
    private static partial System.Text.RegularExpressions.Regex FixedVersionPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial System.Text.RegularExpressions.Regex Sha256Pattern();

    /// <summary>插件来源只接受公开 GitHub 仓库或它的固定 release tag。</summary>
    internal static void ValidatePluginSource(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl) || sourceUrl.Length > 512
            || sourceUrl.Any(char.IsControl)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || uri.IsDefaultPort is false
            || uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is not { Length: 2 or 4 } parts
            || (parts.Length == 4 && !(parts[2] == "releases" && parts[3] == "tag")))
        {
            throw new ZbaBackendException(422, "plugin_source_invalid", "插件来源必须是公开 GitHub 仓库或其固定 release tag。");
        }
    }

    /// <summary>
    /// 设置编辑：键必须与清单里的声明同名；<c>kind</c> 0 保留（不带值）、1 设置、2 删除。
    /// 删除沿用前端序列化形状（<c>value:""</c>），因此 1/2 都要求 value 非空。
    /// </summary>
    internal static void ValidatePluginSettingEdits(IReadOnlyList<ZbaPluginSettingEdit>? edits)
    {
        if (edits is null) throw new ZbaBadRequestException("missing_edits");
        if (edits.Count > 128) throw new ZbaBadRequestException("too_many_edits");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (edit is null) throw new ZbaBadRequestException("invalid_json");
            if (string.IsNullOrWhiteSpace(edit.Key) || edit.Key.Length > 80
                || !PluginSettingKeyPattern().IsMatch(edit.Key) || !keys.Add(edit.Key))
            {
                throw new ZbaBadRequestException("invalid_plugin_setting_key");
            }

            if (edit.Kind is not (0 or 1 or 2)) throw new ZbaBadRequestException("invalid_plugin_setting_kind");
            if (edit.Kind == 0)
            {
                if (edit.Value is not null) throw new ZbaBadRequestException("plugin_setting_value_not_allowed");
                continue;
            }

            if (edit.Value is null) throw new ZbaBadRequestException("missing_plugin_setting_value");
            if (edit.Value.Length > 4096) throw new ZbaBadRequestException("plugin_setting_value_too_large");
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9.-]{0,79}$")]
    private static partial System.Text.RegularExpressions.Regex PluginIdPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,79}$")]
    private static partial System.Text.RegularExpressions.Regex PluginSettingKeyPattern();

    internal static void ValidateSecrets(IReadOnlyList<ZbaSecretEdit> secrets)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var secret in secrets)
        {
            if (secret is null) throw new ZbaBadRequestException("invalid_json");
            if (!Enum.IsDefined(secret.Kind) || !TryResolveSecretTarget(secret, out var target))
                throw new ZbaBadRequestException("invalid_secret_slot");
            if (!targets.Add(target))
                throw new ZbaBadRequestException("duplicate_secret_slot");
            switch (secret.Kind)
            {
                case SecretEditKind.Replace:
                    if (string.IsNullOrEmpty(secret.Value))
                        throw new ZbaBadRequestException("missing_secret_value");
                    if (Encoding.UTF8.GetByteCount(secret.Value) > MaxSecretBytes)
                        throw new ZbaBadRequestException("secret_too_large");
                    break;
                default:
                    if (secret.Value is not null)
                        throw new ZbaBadRequestException("secret_value_not_allowed");
                    break;
            }
        }
    }
}
