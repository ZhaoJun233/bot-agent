using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Platforms;

namespace BotAgent.Services;

public sealed partial class AppSettings
{
    // ---------- NapCat WebUI（面板内扫码登录） ----------
    // 只用于把登录二维码搬进机器人面板；不影响 OneBot 消息通道。

    /// <summary>NapCat WebUI 地址（容器网络里的服务名即可）。环境变量专属。</summary>
    [JsonIgnore]
    public string NapCatWebUiUrl { get; set; } = "http://napcat:6099";

    /// <summary>NapCat WebUI 令牌（napcat/config/webui.json 的 token 字段）。环境变量专属，不写入 settings.json。</summary>
    [JsonIgnore]
    public string NapCatWebUiToken { get; set; } = string.Empty;

    // ---------- 快照 ----------

    /// <summary>
    /// 取一份配置快照（标量浅拷贝；平台策略及其嵌套集合独立复制）。两个用途：
    ///   • **读**：一次处理开始时固定它，处理过程中一律读快照 —— 面板热更新只影响后续处理，
    ///     不会改到在途请求（V3 §5.3）。见 BotAgentHost.GenerateReplyAsync。
    ///   • **写**：<see cref="SettingsBox.Apply" /> 在副本上改完再整体发布（review-findings #4）。
    /// 只读用途时：不要对副本调用保存 / 写回。
    /// </summary>
    public AppSettings Snapshot()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.PlatformPolicies = (PlatformPolicies ?? new List<PlatformPolicySettings>())
            .Where(p => p is not null)
            .Select(p => p.Clone())
            .ToList();
        return copy;
    }
}
