using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Agent;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    /// <summary>模型列表：cfg-0.3.3 起只问服务器 agent 自己的接口（GET &lt;AgentServerBaseUrl&gt;/models）。</summary>
    private async Task HandleAgentModelsAsync(HttpListenerContext context, string method)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_settings.AgentServerBaseUrl)
            ? _settings.ModelBaseUrl
            : _settings.AgentServerBaseUrl;
        var key = string.IsNullOrWhiteSpace(_settings.AgentServerApiKey) ? _settings.ApiKey : _settings.AgentServerApiKey;
        var url = baseUrl.Trim().TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"/chat/completions".Length].TrimEnd('/');
        }
        else if (url.EndsWith("/completions", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"/completions".Length].TrimEnd('/');
        }
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

    /// <summary>Agent 会话管理：cfg-0.3.3 起只列/只改服务器会话；host 历史只读留存。</summary>
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
                var backend = body?["backend"]?.GetValue<string>() ?? "server";
                if (backend != "server")
                {
                    await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "host_backend_removed" });
                    return;
                }

                var created = _agentCmds.CreateAgentSession(chatKey, "server", sessionName);
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
                await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "host_backend_removed" });
                return;

            default:
                await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "action 只支持 new/use/delete/reset/rename" });
                return;
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = ok,
            ["message"] = message,
            ["sessions"] = _agentCmds.BuildAgentSessionsPayload(chatKey)
        });
    }

    /// <summary>服务器 agent 的状态（面板卡片 / 群里的 //status）。</summary>
    private JsonObject BuildAgentStatusPayload()
    {
        return new JsonObject
        {
            ["enabled"] = _settings.EnableAgentBridge,
            ["prefix"] = _settings.AgentPrefix,
            ["allowedUsers"] = _settings.AgentAllowedUsers,
            ["serverAgent"] = _settings.EnableServerAgent,
            ["target"] = "server",
            ["serverBaseUrl"] = _settings.AgentServerBaseUrl,
            ["serverModel"] = _settings.AgentServerModel,
            ["reasoningEffort"] = _settings.AgentReasoningEffort,
            ["reasoningLevels"] = _settings.AgentReasoningLevels,
            ["serverKeepContext"] = _settings.AgentServerKeepContext,
            ["serverTools"] = _settings.AgentServerTools,
            ["serverDocker"] = _settings.AgentServerDocker,
            ["serverWorkdir"] = _settings.AgentServerWorkDir,
            ["serverKeyConfigured"] = !string.IsNullOrWhiteSpace(_settings.AgentServerApiKey),
            ["serverKeySource"] = !string.IsNullOrWhiteSpace(_settings.AgentServerApiKeyOverride)
                ? "panel"
                : (string.IsNullOrWhiteSpace(_settings.AgentServerApiKey) ? "none" : "env"),
            ["mode"] = "server-only"
        };
    }

    /// <summary>面板里的“试一条”：直接把一句提示词送到服务器内置 agent。</summary>
    private async Task HandleAgentTestAsync(HttpListenerContext context)
    {
        var body = await ReadJsonAsync(context);
        if (!TryValidatePanelAgentTarget(body, out var targetError))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = targetError });
            return;
        }

        var prompt = body?["prompt"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "prompt 不能为空" });
            return;
        }

        if (!_settings.EnableServerAgent)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "服务器内置 agent 没开" });
            return;
        }

        var timeout = Math.Clamp(body?["timeoutSec"]?.GetValue<int>() ?? 180, 10, 900);
        var sourceKey = (body?["key"]?.GetValue<string>() ?? "panel:workspace").Trim();
        var sessionId = (body?["sessionId"]?.GetValue<string>() ?? string.Empty).Trim();

        try
        {
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
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = ex.Message });
        }
    }

    /// <summary>
    /// cfg-0.3.3 的 panel test 只允许显式 server（缺省也按 server）。旧客户端可能仍传
    /// host/设备名/auto；这些值必须在建会话、StartRun、模型调用之前拒绝，不能静默改派。
    /// </summary>
    private static bool TryValidatePanelAgentTarget(JsonNode? body, out string error)
    {
        error = "host_backend_removed";
        var node = body?["target"];
        if (node is null)
        {
            return true;
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var requested))
        {
            error = "invalid_target";
            return false;
        }

        var target = (requested ?? string.Empty).Trim();
        if (target.Length == 0
            || target.Equals("server", StringComparison.OrdinalIgnoreCase)
            || target.Equals("服务器", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
