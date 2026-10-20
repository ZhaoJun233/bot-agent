using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Platforms;

namespace BotAgent.Services;

public sealed partial class AppSettings
{
    // ---------- 运维 ----------

    /// <summary>
    /// 列出会话时**脱敏**（群名/昵称/QQ 号）：群里 //sessions //sessions all //runs //pi 的回复、
    /// 面板的会话列表与总览都会遮。默认开（截图/给别人看时不漏隐私）；要原样看就在面板里关掉。
    /// </summary>
    public bool AgentMaskSensitive { get; set; } = true;

    /// <summary>
    /// Agent 附加提示词（面板可改，默认 = <see cref="DefaultAgentPrompt" /> 那条隐私红线）：
    /// **每个 <c>//</c> 任务都会带上它** —— 外部设备（pi）是拼在任务前面，服务器内置 agent 是拼进
    /// 系统提示词。所以“别去读群聊内容 / 成员隐私”不是靠自觉，而是每一轮都随任务下发；
    /// 留空 = 不带任何附加提示词。
    /// </summary>
    public string AgentPrompt { get; set; } = DefaultAgentPrompt;

    /// <summary>系统提示词出厂默认模板。</summary>
    public const string DefaultSystemPrompt = "你是一个在聊天群里的真人群友，说话自然随性，不做作。";

    /// <summary>人设档案出厂默认模板。</summary>
    public const string DefaultPersona = "说话简短、口语化，偶尔吐槽，有自己的喜好与情绪。";

    /// <summary>
    /// 默认的附加提示词：开发/排查时的隐私红线。面板上的「恢复默认」按钮也读它 ——
    /// 要改默认值就改这里（别再在面板文案里抄一份，免得两处漂移）。
    /// </summary>
    public const string DefaultAgentPrompt =
        "【隐私红线（优先级最高）】\n" +
        "1. 不要读取、不要复述聊天内容与成员信息：别直接打开 conversations.json、agent-sessions.json、" +
        "member_profiles/、logs/qqchat.log 里的**对话正文**，也不要把它们粘进回复、提交、测试或文档。\n" +
        "2. 排查报错只看日志里的 ERROR / Exception 堆栈：先把中文（发言、昵称）换成占位符再看，" +
        "message / raw_message 这类文本字段一律不看。\n" +
        "3. 判断数据形状就看字段名、条数、长度、哈希，不看内容。\n" +
        "4. 已经看到的敏感内容不外传：不回群、不写进仓库、不发第三方接口。";

    /// <summary>面板访问令牌（可空）。设置后访问面板与 /api/* 需携带令牌；
    /// /healthz 与 /readyz 不受影响（留给容器健康检查）。
    /// 环境变量专属（QQCHAT_PANEL_TOKEN），不写入 settings.json。</summary>
    [JsonIgnore]
    public string PanelToken { get; set; } = string.Empty;

    // ══════════ 本机 Agent 桥（// 命令，详见 handoff-4 §31）══════════
    //
    // 为什么是“桥”而不是让机器人自己跑：机器人跑在服务器容器里，而 agent 需要的是**管理员本机的能力**
    //（他的代码目录、工具链、pi 的登录态）。所以本机跑一个小进程，主动连到机器人，
    // 收到任务就把它交给本机的 pi CLI，把输出回传 —— 服务器上不装、不跑任何东西。

    /// <summary>总开关。关着的时候 // 开头的消息跟普通消息一样走人设路线（不会进 agent）。</summary>
    public bool EnableAgentBridge { get; set; }

    /// <summary>命令前缀（默认 <c>//</c>）：消息**开头**是这个才当 agent 命令。</summary>
    public string AgentPrefix { get; set; } = "//";

    /// <summary>允许使用 agent 的 QQ 号（逗号/空格/换行分隔）。
    /// **空 = 谁都不能用** —— 这是默认值：这个功能能在管理员电脑上执行命令，宁可先不给任何人。</summary>
    public string AgentAllowedUsers { get; set; } = string.Empty;

    // cfg-0.3.3：以下 host/device 字段只用于解码旧 settings/回退数据；不在 view/change 中暴露，
    // 旧请求会在面板/契约入口拒绝。不要用它们重新启用任何桥或设备执行路径。
    /// <summary>本机 pi 的工作目录（桥侧执行目录）。留空 = 桥自己的默认目录。</summary>
    public string AgentWorkDir { get; set; } = string.Empty;

    /// <summary>传给 pi 的模型（留空 = 本机 pi 默认模型）。这是**没在 AgentDevices 里单独配**时的默认值。</summary>
    public string AgentModel { get; set; } = string.Empty;

    /// <summary>
    /// **每台外部设备的独立配置**（JSON 数组字符串；面板里能增删改）：
    /// <code>[{"name":"ZHAOSPC","enable":true,"model":"vendor/model","workdir":"E:/bot","tools":"","timeoutSec":900}]</code>
    /// 为什么用 JSON 字符串而不是强类型列表：设置是跟面板直接对齐的文本，
    /// 而且容器是 trimmed 发布（反射序列化会抛 resolver 错）—— 字符串零风险。
    /// 泛用性：设备是任意多台、名字任意，面板里加一行就是一台新设备。
    /// </summary>
    public string AgentDevices { get; set; } = string.Empty;

    /// <summary>一面设备的配置（面板里可改；没配的字段就走全局默认）。</summary>
    public sealed record DeviceConfig(
        string Name,
        bool Enable = true,
        string? Model = null,
        string? WorkDir = null,
        string? Tools = null,
        int TimeoutSeconds = 0);

    /// <summary>
    /// 解析 AgentDevices（JSON 数组字符串）。用 JsonDocument 而不是反序列化成类型：
    /// 容器是 trimmed 发布，反射反序列化会抛 TypeInfoResolver 异常（实测踩过）。
    /// </summary>
    public static List<DeviceConfig> ParseDeviceConfigs(string? json)
    {
        var list = new List<DeviceConfig>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? (n.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (name.Length == 0)
                {
                    continue;
                }

                list.Add(new DeviceConfig(
                    name,
                    !item.TryGetProperty("enable", out var e) || e.ValueKind != JsonValueKind.False,
                    Str(item, "model"),
                    Str(item, "workdir"),
                    Str(item, "tools"),
                    Num(item, "timeoutSec")));
            }
        }
        catch (JsonException)
        {
            // 配置写坏了就当没配（不能用一条坏 JSON 让整个 agent 功能不可用）
            return list;
        }

        return list;

        static string? Str(JsonElement obj, string key)
            => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()?.Trim()
                : null;

        static int Num(JsonElement obj, string key)
            => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
    }

    /// <summary>取某台设备的配置（没有就返回 null = 走全局默认）。</summary>
    public DeviceConfig? DeviceConfigFor(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        return ParseDeviceConfigs(AgentDevices)
            .FirstOrDefault(d => string.Equals(d.Name, deviceName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>外部任务和状态使用同一目录优先级：设备配置、全局默认、桥启动目录。</summary>
    public string? ResolveAgentWorkDir(string? deviceName, string? bridgeDirectory = null)
    {
        var configured = DeviceConfigFor(deviceName)?.WorkDir;
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        return !string.IsNullOrWhiteSpace(AgentWorkDir) ? AgentWorkDir.Trim() : bridgeDirectory;
    }

    /// <summary>看设备是否被面板里手动关掉（关了就不派任务，并如实告诉用户）。</summary>
    public bool IsDeviceEnabled(string? deviceName)
        => DeviceConfigFor(deviceName)?.Enable ?? true;


    /// <summary>工具白名单（逗号分隔，对应 pi 的 <c>--tools</c>）。留空 = 本机 pi 默认（全部工具）。</summary>
    public string AgentTools { get; set; } = string.Empty;

    /// <summary>单个任务最长跑多久（秒），超时就让桥把 pi 杀掉。</summary>
    public int AgentTimeoutSeconds { get; set; } = 900;

    /// <summary>回群时单条消息最多多少字（超了就分成几条发）。</summary>
    public int AgentReplyMaxChars { get; set; } = 800;

    /// <summary>任务跑太久时多久报一次“还在跑”（秒；0 = 不报）。</summary>
    public int AgentProgressSeconds { get; set; } = 90;

    /// <summary>同一个会话最多排几个任务（超出直接拒，不让它变无限队列）。</summary>
    public int AgentMaxQueued { get; set; } = 3;

    /// <summary>桥的共享密钥（环境变量专属 QQCHAT_AGENT_TOKEN）。
    /// 这个端口能让人在管理员电脑上执行命令 —— 没设令牌就不接受任何桥连接（不是“默认放行”）。</summary>
    [JsonIgnore]
    public string AgentToken { get; set; } = string.Empty;

    // ── 两个后端 + 路由（管理员 2026-09-17：内外 agent 公用一份白名单）──

    /// <summary>服务器**内置** agent（跑在容器里的工具循环：bash / 读 / 写 / 抓网页）。</summary>
    public bool EnableServerAgent { get; set; } = true;

    /// <summary>**外部设备** agent（管理员电脑上的 pi，通过桥接接入）。
    /// 与服务器 agent 互不影响：不想用哪边就把哪边的开关关掉（不关也能用 //@server / //@host 单次指定）。</summary>
    public bool EnableHostAgent { get; set; } = true;

    /// <summary>任务走哪边（这是个**优先/指定**，两个开关都开时生效）：
    ///   <c>auto</c>（默认）= 外部设备在线就用外部，否则用服务器内置；
    ///   <c>server</c> = 只用服务器内置；
    ///   <c>host</c> = 只用外部设备（离线就如实报错）；
    ///   也可写**设备名**（如 <c>ZHAOSPC</c>）——多台设备时指定用哪台。
    /// 群里还能单条覆盖：<c>//@server 看下日志</c> / <c>//@host 数一下文件</c> / <c>//@ZHAOSPC …</c>。</summary>
    public string AgentTarget { get; set; } = "auto";

    /// <summary>服务器内置 agent 用的模型（空 = 跟聊天用同一个）。</summary>
    public string AgentServerModel { get; set; } = string.Empty;

    /// <summary>服务器 Agent 的推理强度。auto = 不发送额外参数，让模型/供应商自行决定。</summary>
    public string AgentReasoningEffort { get; set; } = "auto";

    /// <summary>
    /// 工作台可选的推理强度值，一行一个。默认值覆盖常见兼容档位；
    /// 供应商不支持某个显式档位时，客户端会退回不带该参数的 auto 请求。
    /// </summary>
    public string AgentReasoningLevels { get; set; } = "auto\nnone\nminimal\nlow\nmedium\nhigh\nxhigh";
}
