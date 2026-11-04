using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

// S54 审批边界回归：真实 HTTP 接线 + 合成模型，仅检查状态、请求计数和工具形状。
public static partial class Program
{
    private static async Task VerifyInheritedApprovalScopesAsync(HttpClient http, string root,
        string adminToken, string readToken, string sessionId, MockOpenAi model)
    {
        foreach (var scopeKind in new[] { "omitted", "empty", "read" })
        {
            var before = model.Requests.Count;
            var body = new JsonObject
            {
                ["sourceKey"] = "panel:workspace", ["sessionId"] = sessionId,
                ["prompt"] = "synthetic zba approval path", ["target"] = "server",
                ["timeoutSeconds"] = 60,
            };
            if (scopeKind == "empty") body["toolScope"] = new JsonArray();
            if (scopeKind == "read") body["toolScope"] = new JsonArray("read");
            var submitted = await ZbaAsync(http, "POST", root + "/tasks", adminToken,
                body.ToJsonString(), idempotencyKey: "synthetic-inherited-scope-" + scopeKind);
            var task = JsonNode.Parse(submitted.Body) as JsonObject;
            var taskId = task?["id"]?.GetValue<string>();
            var needsApproval = scopeKind != "read";
            Check("★ ZBA " + scopeKind + " 作用域按实际可用工具判断审批",
                submitted.Status == 200 && taskId is not null
                && (needsApproval ? task?["state"]?.GetValue<int>() == 2
                    : task?["state"]?.GetValue<int>() != 2),
                "HTTP " + submitted.Status + " state=" + task?["state"]);
            if (taskId is null) continue;

            if (needsApproval)
            {
                Check("★ ZBA " + scopeKind + " 继承 bash 在批准前未调用模型",
                    model.Requests.Count == before, "requests=" + (model.Requests.Count - before));
                var pending = await ZbaAsync(http, "GET", root + "/approvals", readToken);
                var approvalId = ((JsonNode.Parse(pending.Body) as JsonArray) ?? new JsonArray())
                    .OfType<JsonObject>()
                    .FirstOrDefault(a => a["session"]?.GetValue<string>() == "task:" + taskId)?["id"]?.GetValue<string>();
                Check("★ ZBA " + scopeKind + " 继承作用域有可决策的待批单", approvalId is not null);
                if (approvalId is null) continue;
                var approve = scopeKind == "empty";
                var decided = await ZbaAsync(http, "POST", root + "/approvals/" + approvalId + "/decide",
                    adminToken, new JsonObject { ["approve"] = approve }.ToJsonString());
                Check("★ ZBA " + scopeKind + " 继承作用域待批单可决策",
                    decided.Status == 200 && (JsonNode.Parse(decided.Body) as JsonObject)?["ok"]?.GetValue<bool>() == true,
                    "HTTP " + decided.Status);
                var done = await WaitForTaskStateAsync(http, root, readToken, taskId, approve ? 3 : 6, 30);
                Check("★ ZBA " + scopeKind + " 按审批结果结束同一任务",
                    done?["state"]?.GetValue<int>() == (approve ? 3 : 6)
                    && (approve ? model.Requests.Count > before
                        : model.Requests.Count == before && done?["errorCode"]?.GetValue<string>() == "approval_rejected"));
            }
            else
            {
                var done = await WaitForTaskStateAsync(http, root, readToken, taskId, 3, 30);
                Check("★ ZBA 显式只读任务无需审批即可完成", done?["state"]?.GetValue<int>() == 3);
            }
        }
    }
}
