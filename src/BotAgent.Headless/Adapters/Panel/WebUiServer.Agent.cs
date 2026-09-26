using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Adapters.Persistence;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    /// <summary>
    /// 本机 Agent 桥的 WS 接入点。
    /// 为什么要求令牌：这个连接建立后，对方能让 pi 在管理员电脑上干活 —— 不配令牌一律拒，
    /// 不是“回环部署就放行”那种方便口径（handoff-4 §31）。
    /// </summary>
    private async Task HandleAgentBridgeAsync(HttpListenerContext context)
    {
        var bridge = _agentBridge;
        if (bridge is null)
        {
            await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "agent bridge disabled" });
            return;
        }

        var token = _settings.AgentToken?.Trim() ?? string.Empty;
        if (token.Length == 0)
        {
            FileLog.Write("Agent", "agent 桥连接被拒：没有配置 QQCHAT_AGENT_TOKEN（不配令牌不接受任何桥连接）");
            await WriteJsonAsync(context, 403, new JsonObject { ["error"] = "agent token not configured" });
            return;
        }

        var given = context.Request.QueryString["token"] ?? context.Request.Headers["X-Agent-Token"];
        if (!string.Equals(given?.Trim(), token, StringComparison.Ordinal))
        {
            FileLog.Write("Agent", $"agent 桥连接被拒：令牌不对（来自 {context.Request.RemoteEndPoint}）");
            await WriteJsonAsync(context, 401, new JsonObject { ["error"] = "bad token" });
            return;
        }

        if (!context.Request.IsWebSocketRequest)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "websocket required" });
            return;
        }

        HttpListenerWebSocketContext ws;
        try
        {
            ws = await context.AcceptWebSocketAsync(null);
        }
        catch (Exception ex)
        {
            FileLog.Write("Agent", "agent 桥握手失败: " + ex.Message);
            return;
        }

        // 注意：进到 WS 之后**不能再写 HTTP 响应**（AcceptWebSocketAsync 已经把连接拿走了）
        await bridge.HandleAsync(ws.WebSocket, _cts.Token);
    }

    /// <summary>
    /// 模型列表：
    ///   • target=server（或 refresh 为空）→ 问服务器 agent 自己的接口（GET <AgentServerBaseUrl>/models）；
    ///   • device=&lt;设备名&gt; → 让那台外部设备现场重问一遍 pi（`pi --list-models`），然后回列表。
    /// 面板里的下拉就靠它 —— 管理员不用手敲模型名。
    /// </summary>
    private async Task HandleAgentModelsAsync(HttpListenerContext context, string method)
    {
        var query = context.Request.QueryString;
        var target = (query["target"] ?? "server").Trim();
        var bridge = _agentBridge;

        // 外部设备：先让它刷新，再取（刷新是异步的，给它一两秒）
        if (target.Equals("host", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("device", StringComparison.OrdinalIgnoreCase) ||
            (target.Length > 0 && !target.Equals("server", StringComparison.OrdinalIgnoreCase) &&
             !target.Equals("服务器", StringComparison.OrdinalIgnoreCase)))
        {
            var device = target is "host" or "device" ? null : target;
            if (bridge is null || !bridge.Connected)
            {
                await WriteJsonAsync(context, 409, new JsonObject { ["error"] = "外部设备不在线" });
                return;
            }

            await bridge.RequestModelsAsync(device);
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(500);
                var models = bridge.DeviceModels(device);
                if (models.Length > 0)
                {
                    await WriteJsonAsync(context, 200, new JsonObject
                    {
                        ["target"] = "host",
                        ["device"] = device,
                        ["models"] = new JsonArray(models.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray())
                    });
                    return;
                }
            }

            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["target"] = "host",
                ["device"] = device,
                ["models"] = new JsonArray(),
                ["note"] = "设备没说有哪些模型（桥的版本太旧？重启一下 start-pi-bridge.cmd）"
            });
            return;
        }

        // 服务器 agent 的接口里拉 /models
        var baseUrl = string.IsNullOrWhiteSpace(_settings.AgentServerBaseUrl)
            ? _settings.ModelBaseUrl
            : _settings.AgentServerBaseUrl;
        var key = string.IsNullOrWhiteSpace(_settings.AgentServerApiKey) ? _settings.ApiKey : _settings.AgentServerApiKey;
        var url = baseUrl.Trim().TrimEnd('/');
        if (!url.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            url += "/models";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Trim());
            }

            using var response = await _modelProbeHttp.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            var list = new JsonArray();

            // 用 JsonDocument 而不是 JsonNode.Parse：容器是 trimmed/AOT 发布的，
            // JsonNode.Parse 会抛 “JsonSerializerOptions instance must specify a TypeInfoResolver…”（实测）。
            using (var doc = JsonDocument.Parse(body))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idNode) &&
                            idNode.ValueKind == JsonValueKind.String &&
                            idNode.GetString() is { Length: > 0 } id)
                        {
                            // 必须 JsonValue.Create：容器是 trimmed 发布，
                            // list.Add(string) 会造出 JsonValueCustomized<string>，序列化时抛
                            // “JsonSerializerOptions instance must specify a TypeInfoResolver…”（实测）。
                            list.Add(JsonValue.Create(id));
                        }
                    }
                }
            }

            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["target"] = "server",
                ["url"] = url,
                ["models"] = list
            });
        }
        catch (Exception ex)
        {
            // 把完整异常写日志（只回 message 的时候，这类序列化/裁剪问题的栈跟本就看不到）
            FileLog.Warn("Web", $"拉模型列表失败（{url}）：{ex}");
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["target"] = "server",
                ["url"] = url,
                ["models"] = new JsonArray(),
                ["error"] = ex.Message
            });
        }
    }

    /// <summary>把内嵌的 pi-bridge.py 原样吐给下载方（用户本机跑的那个桥）。</summary>
    private static async Task WriteEmbeddedAgentScriptAsync(HttpListenerContext context, string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            await WriteJsonAsync(context, 404, new JsonObject { ["error"] = $"{fileName} not embedded" });
            return;
        }

        await using var stream = assembly.GetManifestResourceStream(name)!;
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/x-python; charset=utf-8";
        context.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");
        await stream.CopyToAsync(context.Response.OutputStream);
        context.Response.Close();
    }

    /// <summary>
    /// 面板的「外部设备」表：在线的 + 只在配置里出现过的（离线）都得列出来 ——
    /// 管理员要能给一台**还没接上来**的设备先写好名字/模型/目录，接上来就直接用。
    /// </summary>
    private JsonArray BuildDeviceListPayload()
    {
        var bridge = _agentBridge;
        var list = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cfg in AppSettings.ParseDeviceConfigs(_settings.AgentDevices))
        {
            var info = bridge?.DeviceInfo(cfg.Name);
            seen.Add(cfg.Name);
            list.Add(new JsonObject
            {
                ["name"] = cfg.Name,
                ["online"] = bridge?.IsDeviceOnline(cfg.Name) ?? false,
                ["enable"] = cfg.Enable,
                ["model"] = cfg.Model ?? string.Empty,
                ["workdir"] = cfg.WorkDir ?? string.Empty,
                ["tools"] = cfg.Tools ?? string.Empty,
                ["timeoutSec"] = cfg.TimeoutSeconds,
                ["pi"] = info?.Pi,
                ["cwd"] = info?.Cwd,
                ["models"] = new JsonArray((bridge?.DeviceModels(cfg.Name) ?? Array.Empty<string>())
                    .Select(m => (JsonNode)JsonValue.Create(m)!).ToArray())
            });
        }

        foreach (var name in bridge?.BridgeNames ?? (IReadOnlyList<string>)Array.Empty<string>())
        {
            if (!seen.Add(name))
            {
                continue;
            }

            var info = bridge!.DeviceInfo(name);
            list.Add(new JsonObject
            {
                ["name"] = name,
                ["online"] = true,
                ["enable"] = true,
                ["model"] = string.Empty,
                // 没在面板里配过的设备：workdir 必须留空（=没配），**不能**拿桥自报的 cwd 充数 ——
                // 否则面板上看着像“设备专属目录 = E:/bot”，而实际生效的是全局目录，改全局怎么都不动。
                ["workdir"] = string.Empty,
                ["tools"] = string.Empty,
                ["timeoutSec"] = 0,
                ["pi"] = info?.Pi,
                ["cwd"] = info?.Cwd,   // 桥自己启动时的工作目录：只作参考
                ["models"] = new JsonArray(bridge.DeviceModels(name).Select(m => (JsonNode)JsonValue.Create(m)!).ToArray())
            });
        }

        return list;
    }

    /// <summary>
    /// 一键连接：给出「本机怎么接上来」的现成脚本（内嵌当前地址与令牌）。
    /// 为什么要内嵌令牌：把管理员的步骤从“改脚本里的令牌 + 改地址”压成“下载 → 双击”一件事。
    /// </summary>
    private async Task HandleAgentSetupAsync(HttpListenerContext context)
    {
        var os = (context.Request.QueryString["os"] ?? "win").Trim().ToLowerInvariant();
        var token = _settings.AgentToken?.Trim() ?? string.Empty;
        var host = context.Request.QueryString["host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            host = context.Request.Headers["Host"] ?? "127.0.0.1:8080";
        }

        var workdir = string.IsNullOrWhiteSpace(_settings.AgentWorkDir) ? "%USERPROFILE%" : _settings.AgentWorkDir;
        var deviceName = (context.Request.QueryString["name"] ?? string.Empty).Trim();
        var wsUrl = $"ws://{host}/agent-bridge";

        string script;
        string filename;
        if (os is "sh" or "linux" or "mac")
        {
            filename = "connect-pi-bridge.sh";
            script =
                "#!/bin/sh\n" +
                "# 一键连接：让这台机器接入 QQ 机器人的 Agent（由机器人的面板生成）\n" +
                "# 需要把 pi-bridge.py 放在同目录（面板里可下载）\n" +
                "set -e\n" +
                $"export PI_BRIDGE_URL='{wsUrl}'\n" +
                $"export PI_BRIDGE_TOKEN='{token}'\n" +
                (deviceName.Length > 0 ? $"export PI_BRIDGE_NAME='{deviceName}'\n" : string.Empty) +
                $"export PI_BRIDGE_WORKDIR='{workdir}'\n" +
                "export PI_BRIDGE_PI=${PI_BRIDGE_PI:-pi}\n" +
                "exec python3 pi-bridge.py\n";
        }
        else
        {
            // ── Windows：**整个文件必须是 ASCII**（注释也用英文）──
            // 为什么：cmd.exe 按“系统 ANSI 代码页”读 .cmd 脚本（中文 Windows 就是 GBK），
            // 而这里给的是 UTF-8 —— 中文注释在那时候会变成乱码，并且能**吃掉紧跟其后的那一行**：
            // 管理员实测 `set PI_BRIDGE_TOKEN=…` 就被吃掉，脚本改成 “--token: expected one argument” 报错。
            // 同理不要在 .cmd 里用 --token %VAR% 那种转一手的形式：参数直接走环境变量，少一个坑。
            filename = "connect-pi-bridge.cmd";
            script =
                "@echo off\r\n" +
                "rem ==========================================================================\r\n" +
                "rem  Connect this PC to the QQ bot's Agent (generated by the bot panel).\r\n" +
                "rem  Put pi-bridge.py in the same folder (download it from the panel too).\r\n" +
                "rem  Keep this file ASCII-only: cmd.exe reads .cmd with the ANSI code page,\r\n" +
                "rem  so non-ASCII comments get garbled and can swallow the next line.\r\n" +
                "rem  Started on demand only - nothing is installed for auto-start.\r\n" +
                "rem ==========================================================================\r\n" +
                "setlocal\r\n" +
                "title pi-bridge (QQ agent)\r\n" +
                "cd /d \"%~dp0\"\r\n" +
                "if not exist \"pi-bridge.py\" (\r\n" +
                "  echo [X] pi-bridge.py not found next to this script.\r\n" +
                "  echo     Download it from the bot panel, then run me again.\r\n" +
                "  pause\r\n" +
                "  exit /b 1\r\n" +
                ")\r\n" +
                $"set \"PI_BRIDGE_URL={wsUrl}\"\r\n" +
                $"set \"PI_BRIDGE_TOKEN={token}\"\r\n" +
                (deviceName.Length > 0 ? $"set \"PI_BRIDGE_NAME={deviceName}\"\r\n" : string.Empty) +
                $"set \"PI_BRIDGE_WORKDIR={workdir}\"\r\n" +
                "python pi-bridge.py\r\n" +
                "echo.\r\n" +
                "echo pi-bridge exited - press any key to close.\r\n" +
                "pause >nul\r\n" +
                "endlocal\r\n";
        }

        var bytes = Encoding.UTF8.GetBytes(script);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{filename}\"");
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    /// <summary>
    /// Agent 会话管理（面板）：
    ///   GET  /api/agent/sessions?key=group:123   → 列该会话的 agent 会话（带当前标记）
    ///   GET  /api/agent/sessions                 → 不带 key：列出**所有**聊天的会话（面板总览用）
    ///   POST {key, action: new|use|delete|reset, id?, name?}
    /// 与群里的 //sessions / //new / //use / //del / //reset 是同一套存储，两边看到的一样。
    /// </summary>
    private async Task HandleAgentSessionsAsync(HttpListenerContext context, string method)
    {
        var key = context.Request.QueryString["key"];

        if (method != "POST")
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                var all = _agentCmds.BuildAllAgentSessionsPayload();
                await WriteJsonAsync(context, 200, all);
                return;
            }

            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["key"] = key,
                ["sessions"] = _agentCmds.BuildAgentSessionsPayload(key!)
            });
            return;
        }

        var body = await ReadJsonAsync(context);
        var action = (body?["action"]?.GetValue<string>() ?? string.Empty).Trim().ToLowerInvariant();
        var chatKey = (body?["key"]?.GetValue<string>() ?? string.Empty).Trim();
        var sessionId = body?["id"]?.GetValue<string>();
        var sessionName = body?["name"]?.GetValue<string>();
        var backend = (body?["backend"]?.GetValue<string>() ?? "host").Trim();

        if (chatKey.Length == 0)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "缺少 key（聊天会话）" });
            return;
        }

        var ok = false;
        var message = string.Empty;
        switch (action)
        {
            case "new":
            {
                var created = _agentCmds.CreateAgentSession(chatKey, backend, sessionName);
                ok = true;
                message = $"已新建会话「{_dto.MaskName(created.Name)}」";
                break;
            }

            case "use":
                ok = _agentCmds.UseAgentSession(chatKey, sessionId ?? string.Empty);
                message = ok ? "已切换" : "没找到这个会话";
                break;

            case "delete":
                ok = _agentCmds.DeleteAgentSession(chatKey, sessionId ?? string.Empty);
                message = ok ? "已删除（当前会话已补新的）" : "没找到这个会话";
                break;

            case "reset":
                ok = _agentCmds.ResetAgentSession(chatKey, sessionId ?? string.Empty);
                message = ok ? "已清空历史" : "没找到这个会话";
                break;

            case "rename":
                ok = _agentCmds.RenameAgentSession(chatKey, sessionId ?? string.Empty, body?["title"]?.GetValue<string>() ?? string.Empty);
                message = ok ? "已改名" : "改名失败（名字空或会话不存在）";
                break;

            case "import":
            {
                // 把设备上 pi 里的一个会话接过来用（新建一个指向它的会话）
                var piId = body?["piSession"]?.GetValue<string>() ?? string.Empty;
                var created = _agentCmds.ImportPiSession(chatKey, piId, sessionName);
                ok = created is not null;
                message = ok ? $"已接用 pi 会话「{_dto.MaskName(created!.Name)}」" : "没认出那个 pi 会话";
                break;
            }

            default:
                await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "action 只支持 new/use/delete/reset" });
                return;
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = ok,
            ["message"] = message,
            ["sessions"] = _agentCmds.BuildAgentSessionsPayload(chatKey)
        });
    }

    /// <summary>本机 agent 的状态（面板卡片 / 群里的 //status）。</summary>
    private JsonObject BuildAgentStatusPayload()
    {
        var bridge = _agentBridge;
        var current = bridge?.Current;
        var target = _settings.AgentTarget;
        // “指定设备”时按名字解析目录 —— 设备离线也要显示它已配的那个目录，
        // 否则面板显示全局默认，而任务实际跑在设备专属目录（两处又对不上）。
        var targetDevice = string.IsNullOrWhiteSpace(target) || target is "auto" or "host" or "server"
            ? null
            : target.Trim();
        var selected = targetDevice is null ? bridge?.AnyBridge : bridge?.DeviceInfo(targetDevice);
        return new JsonObject
        {
            ["enabled"] = _settings.EnableAgentBridge,
            ["prefix"] = _settings.AgentPrefix,
            ["allowedUsers"] = _settings.AgentAllowedUsers,
            ["tokenConfigured"] = !string.IsNullOrWhiteSpace(_settings.AgentToken),
            ["connected"] = bridge?.Connected ?? false,
            ["host"] = selected?.Name ?? targetDevice,
            ["cwd"] = _settings.ResolveAgentWorkDir(targetDevice ?? selected?.Name, selected?.Cwd),
            ["hostCwd"] = selected?.Cwd,
            // 面板要拿来标注“这个目录是哪来的”：设备专属 / 全局默认 / 桥自报
            ["globalWorkdir"] = _settings.AgentWorkDir ?? string.Empty,
            ["serverWorkdir"] = _settings.AgentServerWorkDir,
            ["pi"] = selected?.Pi,
            ["devices"] = new JsonArray(bridge?.BridgeNames.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray() ?? Array.Empty<JsonNode>()),
            ["serverAgent"] = _settings.EnableServerAgent,
            ["hostAgent"] = _settings.EnableHostAgent,
            ["target"] = _settings.AgentTarget,
            ["agentModel"] = _settings.AgentModel,
            ["serverBaseUrl"] = _settings.AgentServerBaseUrl,
            ["serverModel"] = _settings.AgentServerModel,
            ["reasoningEffort"] = _settings.AgentReasoningEffort,
            ["reasoningLevels"] = _settings.AgentReasoningLevels,
            ["serverKeepContext"] = _settings.AgentServerKeepContext,
            ["serverTools"] = _settings.AgentServerTools,
            ["serverDocker"] = _settings.AgentServerDocker,
            ["serverKeyConfigured"] = !string.IsNullOrWhiteSpace(_settings.AgentServerApiKey),
        ["serverKeySource"] = !string.IsNullOrWhiteSpace(_settings.AgentServerApiKeyOverride)
            ? "panel"
            : (string.IsNullOrWhiteSpace(_settings.AgentServerApiKey) ? "none" : "env"),
            ["deviceModels"] = new JsonArray((bridge?.DeviceModels() ?? Array.Empty<string>())
                .Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["deviceList"] = BuildDeviceListPayload(),
            ["queued"] = bridge?.QueuedCount ?? 0,
            ["summary"] = bridge?.Describe() ?? "未启用",
            ["current"] = current is null
                ? null
                : new JsonObject
                {
                    ["id"] = current.Id,
                    ["prompt"] = current.Prompt,
                    ["elapsedMs"] = (long)(Clock.Now - current.StartedAt).TotalMilliseconds,
                    ["note"] = current.LastNote
                }
        };
    }

    /// <summary>面板里的“试一条”：不经过 QQ，直接把一句提示词送到本机 pi，看能不能跑通。</summary>
    private async Task HandleAgentTestAsync(HttpListenerContext context)
    {
        var bridge = _agentBridge;
        var body = await ReadJsonAsync(context);
        var prompt = body?["prompt"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "prompt 不能为空" });
            return;
        }

        var timeout = Math.Clamp(body?["timeoutSec"]?.GetValue<int>() ?? 180, 10, 900);
        var target = (body?["target"]?.GetValue<string>() ?? "host").Trim();
        var sourceKey = (body?["key"]?.GetValue<string>() ?? "panel:workspace").Trim();
        var sessionId = (body?["sessionId"]?.GetValue<string>() ?? string.Empty).Trim();

        // 面板里能分别试两边：服务器内置 agent 直接在容器里跑工具循环（不用经过外部设备）
        if (target.Equals("server", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("服务器", StringComparison.OrdinalIgnoreCase))
        {
            if (!_settings.EnableServerAgent)
            {
                await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "服务器内置 agent 没开" });
                return;
            }

            var serverTask = sessionId.Length > 0
                ? await _agentCmds.RunPanelAgentDirectAsync(sourceKey, sessionId, prompt, timeout, "server")
                : await _agentCmds.RunServerAgentDirectAsync(prompt, timeout);
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = serverTask.Ok,
                ["id"] = serverTask.Id,
                ["text"] = serverTask.Ok ? serverTask.Text : serverTask.Error,
                ["durationMs"] = serverTask.DurationMs,
                ["toolCalls"] = serverTask.ToolCalls,
                ["target"] = "server"
            });
            return;
        }

        if (bridge is null || !_settings.EnableAgentBridge)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "本机 Agent 没启用（面板里打开开关）" });
            return;
        }

        if (!bridge.Connected)
        {
            await WriteJsonAsync(context, 409, new JsonObject
            {
                ["error"] = "外部 agent 设备没连上",
                ["hint"] = "在本机跑 start-pi-bridge.cmd（那个窗口要开着），或者把 target 改成 server 试服务器内置 agent"
            });
            return;
        }

        var task = sessionId.Length > 0
            ? await _agentCmds.RunPanelAgentDirectAsync(sourceKey, sessionId, prompt, timeout, "host")
            : await bridge.RunDirectAsync(prompt, TimeSpan.FromSeconds(timeout), CancellationToken.None);
        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = task.Ok,
            ["id"] = task.Id,
            ["text"] = task.Ok ? task.Text : task.Error,
            ["durationMs"] = task.DurationMs,
            ["toolCalls"] = task.ToolCalls,
            ["target"] = "host"
        });
    }
}
