using BotAgent.Domain.Platforms;
using System.Text.Json.Serialization;

namespace BotAgent.Platforms;

/// <summary>
/// 平台抽象配置切片（包含 OneBot、QQ官方、飞书、本地通道及平台治理策略）。
/// 平台属性的唯一所有者；宿主配置继承此切片，不重复声明同名属性。
/// 运行时凭据的 JSON 排除约束在此维护，适用于宿主和纯平台配置视图。
/// </summary>
public class PlatformOptions
{
    // ---------- OneBot 通道 ----------
    public string OneBotProtocol { get; set; } = "ForwardWebSocket";
    public string OneBotAddress { get; set; } = "ws://127.0.0.1:3001";
    /// <summary>OneBot 访问令牌。运行时凭据，不进入配置 JSON。</summary>
    [JsonIgnore]
    public string OneBotToken { get; set; } = string.Empty;
    public string QuickLoginUin { get; set; } = string.Empty;
    /// <summary>规范化的登录号；派生值不进入配置 JSON。</summary>
    [JsonIgnore]
    public string NormalizedUin => QuickLoginUin?.Trim() ?? string.Empty;
    /// <summary>登录号；解析失败返回 0。</summary>
    [JsonIgnore]
    public long UinOrZero => long.TryParse(NormalizedUin, out var u) ? u : 0;

    // ---------- QQ 官方开放平台通道 ----------
    public bool OfficialEnabled { get; set; }
    public bool OfficialChatEnabled { get; set; } = true;
    public string OfficialAppId { get; set; } = string.Empty;
    /// <summary>QQ 官方 Secret。环境变量专属，不进入配置 JSON。</summary>
    [JsonIgnore]
    public string OfficialAppSecret { get; set; } = string.Empty;
    public bool OfficialSandbox { get; set; }
    /// <summary>官方群白名单使用官方别名号，与私域白名单隔离。</summary>
    public string OfficialWhitelistGroups { get; set; } = string.Empty;
    public string OfficialWhitelistPrivates { get; set; } = string.Empty;
    public string OfficialApiBase { get; set; } = string.Empty;
    public string OfficialTokenUrl { get; set; } = string.Empty;

    // ---------- 飞书通道 ----------
    public bool FeishuEnabled { get; set; }
    public string FeishuAppId { get; set; } = string.Empty;
    /// <summary>飞书应用 Secret。运行时凭据，不进入配置 JSON。</summary>
    [JsonIgnore]
    public string FeishuAppSecret { get; set; } = string.Empty;
    public string FeishuVerificationToken { get; set; } = string.Empty;
    /// <summary>飞书事件签名 Encrypt Key。运行时凭据，不进入配置 JSON。</summary>
    [JsonIgnore]
    public string FeishuEncryptKey { get; set; } = string.Empty;
    /// <summary>飞书通道白名单；空名单拒绝。</summary>
    public string FeishuWhitelist { get; set; } = string.Empty;
    public string FeishuApiBase { get; set; } = string.Empty;

    // ---------- 本地测试通道 ----------
    /// <summary>本地通道名单；空名单不构造通道（fail-closed）。</summary>
    public string LocalChannelIds { get; set; } = string.Empty;

    // ---------- 统一平台策略与白名单 ----------
    /// <summary>私域聊天兼容字段；显式平台策略开关优先，与全局 AI 开关独立。</summary>
    public bool PrivateChatEnabled { get; set; } = true;
    /// <summary>0 = 旧开关交集；1 = 账号级策略开关为准。</summary>
    public int PlatformSwitchSchemaVersion { get; set; }
    /// <summary>按 platformId + accountScope 的显式覆盖；旧字段作为 fallback，不存放凭据。</summary>
    public List<PlatformPolicySettings> PlatformPolicies { get; set; } = new();
    public string WhitelistGroups { get; set; } = string.Empty;
    public string WhitelistPrivates { get; set; } = string.Empty;
    public string MessageWhitelist { get; set; } = string.Empty;

    public void CopyPlatformPropertiesTo(PlatformOptions target)
    {
        target.OneBotProtocol = OneBotProtocol;
        target.OneBotAddress = OneBotAddress;
        target.OneBotToken = OneBotToken;
        target.QuickLoginUin = QuickLoginUin;
        target.OfficialEnabled = OfficialEnabled;
        target.OfficialChatEnabled = OfficialChatEnabled;
        target.OfficialAppId = OfficialAppId;
        target.OfficialAppSecret = OfficialAppSecret;
        target.OfficialSandbox = OfficialSandbox;
        target.OfficialWhitelistGroups = OfficialWhitelistGroups;
        target.OfficialWhitelistPrivates = OfficialWhitelistPrivates;
        target.OfficialApiBase = OfficialApiBase;
        target.OfficialTokenUrl = OfficialTokenUrl;
        target.FeishuEnabled = FeishuEnabled;
        target.FeishuAppId = FeishuAppId;
        target.FeishuAppSecret = FeishuAppSecret;
        target.FeishuVerificationToken = FeishuVerificationToken;
        target.FeishuEncryptKey = FeishuEncryptKey;
        target.FeishuWhitelist = FeishuWhitelist;
        target.FeishuApiBase = FeishuApiBase;
        target.LocalChannelIds = LocalChannelIds;
        target.PrivateChatEnabled = PrivateChatEnabled;
        target.PlatformSwitchSchemaVersion = PlatformSwitchSchemaVersion;
        target.PlatformPolicies = (PlatformPolicies ?? new()).Where(p => p is not null).Select(p => p.Clone()).ToList();
        target.WhitelistGroups = WhitelistGroups;
        target.WhitelistPrivates = WhitelistPrivates;
        target.MessageWhitelist = MessageWhitelist;
    }
}

/// <summary>
/// 平台配置提供者接口（解耦具体的 SettingsBox）。
/// </summary>
public interface IPlatformSettingsAccessor
{
    PlatformOptions Current { get; }
}

public interface IPlatformSettingsBox : IPlatformSettingsAccessor
{
}

/// <summary>
/// 纯平台设置容器。
/// </summary>
public sealed class PlatformSettingsBox(Func<PlatformOptions> getter) : IPlatformSettingsBox
{
    private readonly Func<PlatformOptions> _getter = getter ?? throw new ArgumentNullException(nameof(getter));
    public PlatformSettingsBox(PlatformOptions options) : this(() => options) { }
    public PlatformOptions Current => _getter();
}
