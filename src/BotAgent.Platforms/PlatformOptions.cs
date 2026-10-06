using System.Text.Json.Serialization;
using BotAgent.Domain.Platforms;

namespace BotAgent.Platforms;

/// <summary>
/// 平台抽象配置切片（包含 OneBot、QQ官方、飞书、本地通道及平台治理策略）。
/// </summary>
public class PlatformOptions
{
    // ---------- OneBot 通道 ----------
    public string OneBotProtocol { get; set; } = "ForwardWebSocket";
    public string OneBotAddress { get; set; } = "ws://127.0.0.1:3001";
    /// <summary>OneBot 访问令牌。环境变量专属（QQCHAT_ONEBOT_TOKEN），不写入 settings.json。</summary>
    [JsonIgnore]
    public string OneBotToken { get; set; } = string.Empty;
    public string QuickLoginUin { get; set; } = string.Empty;
    /// <summary>规范化后的登录 QQ 号（空字符串按未配置处理）。</summary>
    [JsonIgnore]
    public string NormalizedUin => QuickLoginUin?.Trim() ?? string.Empty;
    /// <summary>登录 QQ 号；解析失败返回 0。</summary>
    [JsonIgnore]
    public long UinOrZero => long.TryParse(NormalizedUin, out var u) ? u : 0;

    // ---------- QQ 官方开放平台通道 ----------
    public bool OfficialEnabled { get; set; }
    public bool OfficialChatEnabled { get; set; } = true;
    public string OfficialAppId { get; set; } = string.Empty;
    /// <summary>开放平台的机器人 secret。密钥：只从环境变量读，不落盘。</summary>
    [JsonIgnore]
    public string OfficialAppSecret { get; set; } = string.Empty;
    public bool OfficialSandbox { get; set; }
    public string OfficialWhitelistGroups { get; set; } = string.Empty;
    public string OfficialWhitelistPrivates { get; set; } = string.Empty;
    public string OfficialApiBase { get; set; } = string.Empty;
    public string OfficialTokenUrl { get; set; } = string.Empty;

    // ---------- 飞书通道 ----------
    public bool FeishuEnabled { get; set; }
    public string FeishuAppId { get; set; } = string.Empty;
    /// <summary>飞书应用 App Secret（只从环境变量或密钥库读，不落 settings.json）。</summary>
    [JsonIgnore]
    public string FeishuAppSecret { get; set; } = string.Empty;
    /// <summary>飞书事件回调 Verification Token（沿用旧配置的持久化语义）。</summary>
    public string FeishuVerificationToken { get; set; } = string.Empty;
    /// <summary>飞书事件签名 Encrypt Key（可选，只从环境变量或密钥库读）。</summary>
    [JsonIgnore]
    public string FeishuEncryptKey { get; set; } = string.Empty;
    /// <summary>飞书通道白名单（群 chat_id / 用户 open_id 或内部别名号，逗号分隔；空 = 拒绝）。</summary>
    public string FeishuWhitelist { get; set; } = string.Empty;
    public string FeishuApiBase { get; set; } = string.Empty;

    // ---------- 本地测试通道 ----------
    /// <summary>本地 id 名单（逗号分隔）；空 = 整个通道都不建（默认关，fail-closed）。</summary>
    public string LocalChannelIds { get; set; } = string.Empty;

    // ---------- 统一平台策略与白名单 ----------
    /// <summary>私域聊天的兼容字段；显式平台策略开关优先。与全局 AI 开关独立。</summary>
    public bool PrivateChatEnabled { get; set; } = true;
    /// <summary>0 = legacy switch intersection; 1 = account-scoped policy switches are authoritative.</summary>
    public int PlatformSwitchSchemaVersion { get; set; }
    /// <summary>平台策略的显式覆盖。旧字段仍是兼容 fallback；凭据永远不进入这里。</summary>
    public List<PlatformPolicySettings> PlatformPolicies { get; set; } = new();
    /// <summary>群聊白名单（每行/逗号分隔群号；* = 所有群）。留空 = 回落到旧的共用名单。</summary>
    public string WhitelistGroups { get; set; } = string.Empty;
    /// <summary>私聊白名单（每行/逗号分隔 QQ 号；* = 所有人）。留空 = 回落到旧的共用名单。</summary>
    public string WhitelistPrivates { get; set; } = string.Empty;
    /// <summary>旧的共用消息白名单；新白名单留空时回落到这里，留空 = 全部忽略。</summary>
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
