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
using BotAgent.Services.Conversations;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Adapters.Persistence;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    /// <summary>
    /// POST /api/restart：一键重启（把配置里“重启才生效”的部分落地）。
    /// 先回 200 再退进程，否则浏览器只会看到一个断掉的请求；容器带着 restart 策略会自己回来，
    /// 面板那边按跟“一键部署”同一个办法——轮询 <c>/healthz</c> 等它回来。
    /// </summary>
    private async Task HandleRestartAsync(HttpListenerContext context, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "用法：POST /api/restart" });
            return;
        }

        if (_onRestart is null)
        {
            // 集成测试里不会传这个回调：如实说“这里不会真重启”，而不是让测试意外把进程弄死
            FileLog.Write("Web", "面板请求重启，但当前进程没接重启回调（测试环境）→ 忽略");
            await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true, ["restarting"] = false, ["note"] = "当前进程不支持重启" });
            return;
        }

        FileLog.Write("Web", "面板点了「一键重启」→ 进程即将退出，容器会按 restart 策略拉起来");

        // 关键顺序：**先把退出排上**，再去写响应。
        // 反过来的话，写响应一旦失败（实测：客户端 POST 没带 Content-Length 时 HttpListener 会回 411）
        // 异常会往上抛，重启回调就永远不会执行 —— 用户看到“点了没反应”，而日志里却写着“即将退出”。
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            try
            {
                _onRestart();
            }
            catch (Exception ex)
            {
                FileLog.Write("Web", "重启回调抛异常：" + ex.Message);
            }
        });

        try
        {
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["restarting"] = true,
                ["note"] = "进程马上退出，容器会在几秒内自己起来（页面会自动等它）",
            });
        }
        catch (Exception ex)
        {
            // 响应没写出去无所谓：重启已经在路上了
            FileLog.Write("Web", "重启响应没写出去（不影响重启）：" + ex.Message);
        }
    }

    private JsonObject BuildStatus() => new()
    {
        ["status"] = "running",
        ["uptimeSeconds"] = (int)(Clock.Now - _startedAt).TotalSeconds,
        ["startedAt"] = _startedAt.ToString("O"),
        ["onebot"] = new JsonObject
        {
            ["connected"] = _gateway.IsConnected,
            ["protocol"] = _settings.OneBotProtocol,
            ["address"] = _settings.OneBotAddress
        },
        ["account"] = new JsonObject
        {
            ["uin"] = _settings.NormalizedUin,
            ["selfId"] = _identity.SelfId,
            // true/false = 已探明；null = 未知（协议端未实现 get_status）
            // 注意：这是“QQ 账号在不在线”，与上面的 onebot.connected 不是一回事
            ["online"] = _scheduler.AccountOnline
        },
        ["login"] = new JsonObject
        {
            // 面板靠这两项决定「扫码卡片」里是显示二维码还是显示原因
            ["qrAvailable"] = _loginQr.Configured,
            ["napcatWebUi"] = _loginQr.WebUiDisplay
        },
        ["agent"] = new JsonObject
        {
            ["enabled"] = _settings.AiModeEnabled,
            ["model"] = _settings.Model,
            ["desire"] = _settings.AiDesire,
            ["personaConfigured"] = !string.IsNullOrWhiteSpace(_settings.BotPersona)
        },
        ["conversations"] = _registry.Snapshot().Count,
        ["inFlightReplies"] = _reply.InFlightReplies,
        ["queuedReplies"] = _reply.QueuedReplies,
        ["profileSummaries"] = _scheduler.SummaryDoneCount,
        ["profileSummaryFailures"] = _scheduler.SummaryFailCount,
        ["stickers"] = _stickers.Store.Count,
        ["stickersDescribed"] = _stickers.Store.DescribedCount,
        ["stickersPending"] = _stickers.PendingDescribe
    };

    private JsonObject BuildState() => new()
    {
        ["status"] = BuildStatus(),
        ["aiMode"] = _settings.AiModeEnabled,
        ["conversations"] = new JsonArray(BuildConversations().Select(c => (JsonNode)c).ToArray()),
        ["serverTime"] = Clock.Now.ToUnixTimeMilliseconds()
    };

    /// <summary>
    /// GET /api/health-report：面板卡片要的一切（开关/时刻/收件人/下一次推送/上次结果）。
    /// 这里**不做探针**（不碰模型接口、不碰 TTS）—— 打开面板就慢 6 秒不可接受；
    /// 「预览这次会发什么」是用户主动点按钮才走 <see cref="HandleHealthReportAsync" />。
    /// </summary>
    private JsonObject BuildHealthReportPayload()
    {
        var (hour, minute) = AppSettings.ParseHealthReportClock(_settings.HealthReportTime);
        var next = _healthReports?.NextRunAt;
        return new JsonObject
        {
            ["enabled"] = _settings.HealthReportEnabled,
            ["time"] = $"{hour:00}:{minute:00}",
            ["targets"] = _settings.HealthReportTargets ?? string.Empty,
            ["targetList"] = new JsonArray(HealthReportService.ParseTargets(_settings.HealthReportTargets)
                .Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
            ["nextRunAt"] = next?.ToString("O"),
            ["lastSentAt"] = _healthReports?.LastSentAt?.ToString("O"),
            ["lastError"] = _healthReports?.LastError,
            ["sentCount"] = _healthReports?.SentCount ?? 0,
            ["serverTime"] = HealthReportService.NowBeijing().ToString("O")
        };
    }

    /// <summary>
    /// POST /api/health-report：<c>{"mode":"preview"}</c> 只生成（不碰 QQ）、
    /// <c>{"mode":"send"}</c>（默认）现在真发一条到私聊。
    /// 面板两个按钮走这里；两个都要能被当成“当场验收”，所以返回正文与失败原因。
    /// </summary>
    private async Task HandleHealthReportAsync(HttpListenerContext context)
    {
        if (_healthReports is null)
        {
            await WriteJsonAsync(context, 503, new JsonObject { ["ok"] = false, ["error"] = "健康日报服务未初始化" });
            return;
        }

        JsonNode? body = null;
        try
        {
            body = await ReadJsonAsync(context);
        }
        catch (Exception)
        {
            // 没带请求体 = 默认“发一条”
        }

        var mode = (body?["mode"]?.ToString() ?? "send").Trim().ToLowerInvariant();
        if (mode == "preview")
        {
            var preview = await _healthReports.PreviewAsync();
            await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true, ["mode"] = "preview", ["text"] = preview });
            return;
        }

        var (ok, text, error) = await _healthReports.SendNowAsync("面板手动");
        await WriteJsonAsync(context, ok ? 200 : 502, new JsonObject
        {
            ["ok"] = ok,
            ["mode"] = "send",
            ["text"] = text,
            ["error"] = error,
            ["targets"] = new JsonArray(HealthReportService.ParseTargets(_settings.HealthReportTargets)
                .Select(id => (JsonNode)JsonValue.Create(id)).ToArray())
        });
    }
}
