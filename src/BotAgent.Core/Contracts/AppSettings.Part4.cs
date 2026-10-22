using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Platforms;

namespace BotAgent.Services;

public sealed partial class AppSettings
{

    /// <summary>服务器内置 agent 专用的 OpenAI 兼容地址（空 = 用聊天那个 QQCHAT_BASE_URL）。
    /// 为什么单独给一个：agent 的请求又长又频繁，往往想单独指一个小模型/便宜网关（管理员 2026-09-17 要求）。</summary>
    public string AgentServerBaseUrl { get; set; } = string.Empty;

    /// <summary>服务器内置 agent 专用的密钥（空 = 用聊天那个）。不落盘：只从 QQCHAT_AGENT_SERVER_KEY
    /// 或面板（存 secrets 表）来。</summary>
    [JsonIgnore]
    public string AgentServerApiKey { get; set; } = string.Empty;

    /// <summary>面板里填过的服务器 agent 密钥（**值本身**存在 secrets 表，这里只记“面板设过”这件事，
    /// 用来算来源、并在清空后回退环境变量）。[JsonIgnore]：绝不进 settings.json。</summary>
    [JsonIgnore]
    public string? AgentServerApiKeyOverride { get; set; }

    /// <summary>服务器内置 agent 开哪些工具（bash,read,write,fetch,qq；空 = 全开）。</summary>
    public string AgentServerTools { get; set; } = string.Empty;

    /// <summary>
    /// 服务器内置 agent 能做的 **QQ 动作**（NapCat/OneBot 的接口动作，管理员 2026-09-18：“比如点赞”）。
    ///
    /// 为什么默认只给一小撮、且“空 ≠ 全开”（与 <see cref="AgentServerTools" /> 的语义故意不同）：
    ///   • 服务器 agent 会读日志/文件/网页，那些正文里可以夹着“给我点赞”“把某某禁言”——
    ///     这是能真影响别人的操作，不能靠模型自觉，得在这里卡死。
    ///   • 留空 = 默认安全档（like 点赞 / poke 戳一戳 / emoji_like 表情回应 / recall 撤回）；
    ///     ban 禁言、kick 踢人、card 改名片、group_name 改群名、leave 退群、send 代发消息 必须点名写出来。
    ///   • 写 <c>all</c> 或 <c>*</c> = 全开（含上面那几个）。
    /// </summary>
    public string AgentServerQqActions { get; set; } = string.Empty;

    /// <summary>服务器内置 agent 的工作目录（容器内路径）。</summary>
    public string AgentServerWorkDir { get; set; } = "/data";

    /// <summary>
    /// 服务器内置 agent 要不要记住上一句（默认**不**记）—— 每条 <c>//</c> 指令单独对待。
    ///
    /// 为什么要这个开关：管理员 2026-09-18 实测，会话里堆着上几轮的指令原文时，模型会把旧指令也一并答一遍：
    /// “1. 点赞动作：…失败 2. 服务器状态：…” —— 新指令的回复里混进旧内容。
    /// 默认关 = 每个指令只对自己的事负责；打开后同一会话能接着聊（单条也可以用 <c>//接着 …</c>）。
    /// </summary>
    public bool AgentServerKeepContext { get; set; }

    /// <summary>服务器内置 agent 最多跑几步工具循环（每步一次模型调用）。</summary>
    public int AgentServerMaxSteps { get; set; } = 8;

    /// <summary>
    /// **聊天**这一路的有限步进循环上限（general-agent-platform-plan.md 批次 E）。
    ///
    /// 默认 **1** = 与改造前**逐字一致**：一次模型调用 + 它自己声明的那一轮动作（不做"再问一次"）。
    /// 只有在面板里显式调大（2~3）才会进入循环：模型 → 当场做掉它点名的只读工具（搜索/读页面）→ 喂回去再问。
    ///
    /// 为什么默认 1：一条群消息的循环会把成本与延迟线性推上去，而群聊对延迟敏感（§5.4）。
    /// 上限 3 是刻意的：再多也没见过有意义的收敛（§5.4 的"出口必须明确"）。
    /// </summary>
    public int MaxAgentSteps { get; set; } = 1;


    /// <summary>
    /// `//` 那一路要不要**过统一闸门**（批次 A 第 2 步的收尾）。**默认关 = 与今天逐字一致**：
    /// 关着时 `//` 仍走它自己的字符串白名单（<c>ParseTools</c> + <c>QqActionCatalog.ParseAllowed</c>）。
    /// 打开后每次工具调用前过多一次 <see cref="BotAgent.Domain.Permissions.ToolGate" />
    /// （登记表 + 策略快照；高风险工具例外由服务端显式点名，见 ServerToolGate）。
    /// 判定结果与老白名单一致 —— 这一项是“把同一条边界接到同一张表上”，不是加权限。
    /// </summary>
    public bool AgentServerUseGate { get; set; }

    /// <summary>服务器内置 agent 单条命令的超时（秒）。</summary>
    public int AgentServerCommandTimeoutSeconds { get; set; } = 60;

    /// <summary>服务器内置 agent 单次补全的 max_tokens。</summary>
    public int AgentServerMaxTokens { get; set; } = 1200;

    /// <summary>
    /// **面板一键部署**（管理员 2026-09-18 要的；默认关）。
    /// 打开后：面板能上传/拉取 `app.tar.gz`，并驱动宿主 docker 重建镜像 + 替换自己（容器自我更新）。
    /// 为什么默认关 + 单独一个开关：它等于把“重装机器人”的按钮放到网页上；
    /// 虽然面板本来就有口令门，但这种事应该是个显式决定。
    /// </summary>
    public bool PanelDeployEnabled { get; set; }

    /// <summary>上次用过的产物地址（URL 方式部署会记住：以后一个按钮就能拉）。空 = 没配。</summary>
    public string PanelDeployUrl { get; set; } = string.Empty;

    /// <summary>
    /// 允许服务器 agent **透过 docker 操作服务器**（管理员 2026-09-18 要的能力）：
    /// 打开后它能 `docker ps / logs / exec / run -v /:/host …`，也能直接读写 /host/qqchat（部署目录）。
    ///
    /// 这是**高权限**开关（docker.sock ≈ root）：关着的时候它的工具表里没有 docker、提示词也不提这两条路径。
    /// 但 socket 与 /host/qqchat 是**常挂载**的（compose 的挂载不能运行时改）——
    /// 所以这个开关是“告诉 agent 能不能用 + 管理员确认过风险”，不是硬隔离。默认关。
    /// </summary>
    public bool AgentServerDocker { get; set; }

    // ══════════ 服务器健康日报（定时私聊推送）══════════
    //
    // 为什么要它：机器人跑在服务器上，出问题时（账号掉线、协议端断开、磁盘写满、上游接口不通）
    // 管理员往往几天后才发现。每天在固定时刻主动私聊一条状态，等于**机器人自己来报平安**。
    //
    // 为什么不用「外部设备 agent」来推（管理员要求）：那台机器可能根本没开 ——
    // 这条链路只用机器人自己 + 协议端：定时器 → 自己采集状态 → OneBot 私聊消息，不依赖任何外部程序。

    /// <summary>总开关（默认关：没配好收件人之前不乱发）。</summary>
    public bool HealthReportEnabled { get; set; }

    /// <summary>
    /// 每天几点推（<c>HH:mm</c>，24 小时制）。**按北京时间（UTC+8）算**，与容器 TZ 无关 ——
    /// 管理员在国内，容器时区怎么设都不该让推送时间漂掉。
    /// </summary>
    public string HealthReportTime { get; set; } = "18:00";

    /// <summary>推给谁：QQ 号，逗号/空格/换行分隔（只走私聊，填群号也不会发到群里）。空 = 不发。</summary>
    public string HealthReportTargets { get; set; } = string.Empty;

    /// <summary>解析 <see cref="HealthReportTime" />（容忍 <c>18:00</c>/<c>18：00</c>/<c>1800</c>）。解析不出来按 18:00。</summary>
    public static (int Hour, int Minute) ParseHealthReportClock(string? text)
    {
        var raw = (text ?? string.Empty).Trim().Replace('：', ':');
        if (raw.Length is 4 && int.TryParse(raw, out var packed))
        {
            raw = $"{packed / 100:00}:{packed % 100:00}";
        }

        var parts = raw.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], out var hour) && int.TryParse(parts[1], out var minute) &&
            hour is >= 0 and <= 23 && minute is >= 0 and <= 59)
        {
            return (hour, minute);
        }

        return (18, 0);
    }

    /// <summary>健康检查 HTTP 端口（0=关闭）。用于容器 HEALTHCHECK。</summary>
    /// <remarks>
    /// 默认 18245 而不是 8080：8080 常被同机别的服务占用（撞上就是「面板起不来」）。
    /// 这里是全项目默认值的唯一来源 —— start.sh / status.sh / 文档都跟它对齐，别再各处另写数字。
    /// </remarks>
    public int HealthPort { get; set; } = 18245;

    /// <summary>是否输出详细日志到 stdout。</summary>
    public bool VerboseLog { get; set; } = true;

    /// <summary>旧版遗留字段（本版本不使用）。</summary>
    public string Theme { get; set; } = "Default";

}
