using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Services.Permissions;

namespace BotAgent.Adapters.Panel;

/// <summary>
/// 面板上的人在回路审批（general-agent-platform-plan.md 批次 I）。
///
/// **这是一条高权限写路径**（它能让“需要批准的工具”真的走完执行），所以前置条件是 fail-closed 的，
/// 与 <c>AgentAllowedUsers</c> “空 = 谁都不能用”同一条纪律：
///   · **未配置面板令牌 → 一律不许批**（未配令牌时面板对回环是全开的，那时放行这条路径等于把执行权交出去）；
///   · **审批总开关关着 → 一律不许批**（按钮也不出现，默认关）；
///   · 判定本身**一条都不放宽**：走 <see cref="ApprovalUseCase.PanelDecide" />，
///     它内部仍是 <c>ApprovalFlow.Handle</c>（身份 / 有效期 / 一次性 / 策略版本）+ 执行前再过一次闸门。
///
/// 只读那一半（待批单列表）不写任何东西：只有编号 / 工具名 / 已脱敏摘要 / 脱敏后的会话 key / 剩余秒数。
/// </summary>
public sealed partial class WebUiServer
{
    private JsonObject BuildApprovalsPayload()
    {
        var s = _box.Current;
        var tokenConfigured = !string.IsNullOrWhiteSpace(s.PanelToken);
        var pending = new JsonArray();

        if (_approvals is not null)
        {
            var now = Clock.Now;
            foreach (var request in _approvals.PendingApprovals())
            {
                pending.Add(new JsonObject
                {
                    ["id"] = request.RequestId,
                    ["tool"] = request.ToolId,
                    // 摘要的脱敏在建单时就做了（ApprovalStore.RedactSummary）——这里不再二次加工
                    ["summary"] = request.Summary,
                    ["key"] = _dto.Mask(request.ConversationKey),
                    ["expiresInSeconds"] = (int)Math.Max(0, (request.ExpiresAt - now).TotalSeconds),
                    ["policyVersion"] = request.PolicyVersion,
                });
            }
        }

        return new JsonObject
        {
            ["available"] = _approvals is not null,
            ["enabled"] = s.EnableApprovals,
            ["tokenConfigured"] = tokenConfigured,
            // 面板上的按钮只在 canDecide 为真时出现 —— 服务端也按同一个条件拒绝（两道一致，不是只有 UI 拦）
            ["canDecide"] = s.EnableApprovals && tokenConfigured && _approvals is not null,
            ["pending"] = pending,
        };
    }

    private async Task HandleApprovalDecideAsync(HttpListenerContext context)
    {
        var s = _box.Current;

        if (!s.EnableApprovals)
        {
            await WriteJsonAsync(context, 403, new JsonObject
            {
                ["error"] = "人工审批总开关是关的（默认关）",
                ["reason"] = "approvals_disabled",
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(s.PanelToken))
        {
            // fail-closed：未配面板令牌时面板对回环是全开的，这条路径不能顺带被放开
            await WriteJsonAsync(context, 403, new JsonObject
            {
                ["error"] = "未配置面板令牌 → 面板审批不可用（先在设置里配一个）",
                ["reason"] = "panel_token_required",
            });
            return;
        }

        if (_approvals is null)
        {
            await WriteJsonAsync(context, 503, new JsonObject { ["error"] = "审批组件不可用", ["reason"] = "no_store" });
            return;
        }

        var body = await ReadJsonAsync(context) as JsonObject;
        var id = (body?["id"]?.GetValue<string>() ?? string.Empty).Trim();
        var approve = body?["approve"]?.GetValue<bool>() ?? false;
        if (id.Length == 0)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "缺 id", ["reason"] = "bad_request" });
            return;
        }

        var (ok, reason) = _approvals.PanelDecide(id, approve);
        // unknown_request / not_an_approver / expired / already_decided 一律如实回报 ——
        // 面板只显示结果，不改判定（判定在用例里，与群里那条路同一份）。
        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = ok,
            ["decided"] = approve ? "approve" : "reject",
            ["reason"] = reason,
        });
    }
}
