using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BotAgent.Panel;

internal static partial class ZbaOpenApi
{
    internal static JsonObject Build()
    {
        var paths = new JsonObject();
        foreach (var route in ZbaRoutes.All)
        {
            var path = paths[route.Template] as JsonObject ?? new JsonObject();
            paths[route.Template] = path;

            var operation = new JsonObject
            {
                ["operationId"] = route.OperationId,
                ["summary"] = route.Summary,
                ["tags"] = new JsonArray(Tag(route.Template)),
                ["security"] = route.Public ? new JsonArray() : new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }),
                ["responses"] = Responses(route),
                ["x-zba-scope"] = route.Scope == ZbaScope.Admin ? "admin" : "read"
            };

            var parameters = Parameters(route.Template);
            if (parameters.Count > 0) operation["parameters"] = parameters;
            if (route.RequestSchema is not null)
            {
                operation["requestBody"] = new JsonObject
                {
                    ["required"] = true,
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = Ref(route.RequestSchema)
                        }
                    }
                };
            }

            path[route.Method.ToLowerInvariant()] = operation;
        }

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "Z-bot Backend API",
                ["version"] = ZbaContract.Version,
                ["description"] = "ZBA v1. 认证仅使用 Bearer；写路径不使用 Cookie。未列出的路径、字段和动作一律拒绝。"
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "/" }),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["bearerAuth"] = new JsonObject
                    {
                        ["type"] = "http",
                        ["scheme"] = "bearer",
                        ["description"] = "BOTAGENT_ZBA_TOKEN 为管理员；BOTAGENT_ZBA_READ_TOKEN 为只读。"
                    }
                },
                ["schemas"] = Schemas()
            }
        };
    }

    private static JsonArray Parameters(string template)
    {
        var parameters = new JsonArray();
        foreach (Match match in ParameterPattern().Matches(template))
        {
            parameters.Add(new JsonObject
            {
                ["name"] = match.Groups[1].Value,
                ["in"] = "path",
                ["required"] = true,
                ["schema"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^[A-Za-z0-9._~-]{1,128}$"
                }
            });
        }

        return parameters;
    }

    private static JsonObject Responses(ZbaRouteInfo route) => new()
    {
        ["200"] = new JsonObject
        {
            ["description"] = "成功",
            ["content"] = new JsonObject
            {
                ["application/json"] = new JsonObject { ["schema"] = Ref(route.ResponseSchema) }
            }
        },
        ["400"] = Error("请求无效"),
        ["401"] = Error("未认证"),
        ["403"] = Error("权限不足"),
        ["404"] = Error("不存在"),
        ["405"] = Error("方法不允许"),
        ["409"] = Error("版本冲突 / 动作不支持"),
        ["410"] = Error("操作键已过期"),
        ["422"] = Error("动作不支持"),
        ["429"] = Error("容量或认证失败次数超限"),
        ["503"] = Error("存储不可用或服务未配置")
    };

    private static JsonObject Error(string description) => new()
    {
        ["description"] = description,
        ["content"] = new JsonObject
        {
            ["application/json"] = new JsonObject { ["schema"] = Ref("ZbaErrorBody") }
        }
    };

    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/components/schemas/" + name };

    private static string Tag(string template)
    {
        var parts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[2] : "zba";
    }

    private static JsonObject Schemas() => new()
    {
        ["ZbaErrorBody"] = Object(("error", Ref("ZbaError"))),
        ["ZbaError"] = Object(("code", String()), ("message", String()), ("reason", NullableString()), ("backendCode", Integer())),
        ["ZbaHealth"] = Object(("status", String()), ("ready", Boolean()), ("configured", Boolean()),
            ("contractVersion", String()), ("checkedAt", String())),
        ["ZbaMeta"] = Object(("contractVersion", String()), ("serverVersion", String()), ("authentication", String()),
            ("scopes", Array(String())), ("sse", Boolean()), ("mcpManagement", Boolean()),
            ("dockerManagement", Boolean()), ("maxBodyBytes", Integer())),
        ["ZbaStatus"] = Object(("state", String()), ("ready", Boolean()), ("version", String()),
            ("startedAt", String()), ("updatedAt", String()), ("configurationVersion", NullableString()),
            ("configurationApplyState", String()), ("components", Array(Ref("ZbaComponent"))),
            ("recentErrors", Array(Ref("ZbaErrorSummary")))),
        ["ZbaComponent"] = Object(("id", String()), ("kind", String()), ("displayName", String()),
            ("state", String()), ("available", Boolean()), ("reasonCode", NullableString()), ("updatedAt", NullableString())),
        ["ZbaErrorSummary"] = Object(("at", String()), ("scope", String()), ("code", String())),
        ["ZbaConfigurationReadRequest"] = RequiredObject(new[] { "kind" }, ("kind", Integer())),
        ["ZbaConfigurationFindRequest"] = RequiredObject(new[] { "key" }, ("key", Ref("ConfigurationOperationKey"))),
        ["ZbaCapabilityInspectRequest"] = RequiredObject(new[] { "key" }, ("key", Ref("ManagedObjectKey"))),
        ["ZbaConfigurationSaveRequest"] = RequiredObject(
            new[] { "contractRevision", "expectedVersion", "key", "changes", "secrets" },
            ("contractRevision", String()), ("expectedVersion", String()), ("key", Ref("ConfigurationOperationKey")),
            ("changes", Ref("ConfigurationPatch")), ("secrets", Array(Ref("ZbaSecretEdit")))),
        ["ZbaSecretEdit"] = RequiredObject(new[] { "slot", "kind" },
            ("slot", Integer()), ("kind", Integer()), ("value", NullableString())),
        ["ConfigurationOperationKey"] = new JsonObject
        {
            ["type"] = "string",
            ["pattern"] = "^cfg1:[0-9a-f]{32}:[0-9]+:[0-9a-f]{32}$",
            ["description"] = "配置操作键；缺失或格式不符会被拒绝。"
        },
        ["ManagedOperationKey"] = new JsonObject
        {
            ["type"] = "string",
            ["pattern"] = "^mg1:[0-9a-f]{32}:[0-9]+:[0-9a-f]{32}$",
            ["description"] = "管理操作键；缺失或格式不符会被拒绝。"
        },
        ["ManagedObjectKey"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "kind = driver | plugin | provider | device | mcp；具体键字段随 kind 变化。",
            ["required"] = new JsonArray("kind"),
            ["properties"] = new JsonObject { ["kind"] = String(), ["key"] = new JsonObject { ["type"] = "object" } }
        },
        ["ManagedPageRequest"] = Object(("kind", Integer()), ("limit", Integer()), ("cursor", NullableString())),
        ["ConfigurationPatch"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Core ConfigurationPatch；未列出的字段保持 Keep，未知字段拒绝。",
            ["additionalProperties"] = true
        },
        ["ZbaConfigurationValidationRequest"] = RequiredObject(
            new[] { "contractRevision", "expectedVersion", "changes", "secrets" },
            ("contractRevision", String()), ("expectedVersion", String()), ("changes", Ref("ConfigurationPatch")),
            ("secrets", Array(Ref("ZbaSecretEdit")))),
        ["ManagedExecuteRequest"] = RequiredObject(
            new[] { "contractRevision", "expectedDescriptorRevision", "key", "command" },
            ("contractRevision", String()), ("expectedDescriptorRevision", String()),
            ("key", Ref("ManagedOperationKey")), ("command", new JsonObject { ["type"] = "object" })),
        ["ManagedPage"] = Object(("code", Integer()), ("items", Array(Ref("ManagedDescriptor"))),
            ("nextCursor", NullableString()), ("descriptorRevision", String())),
        ["ManagedDescriptor"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
        ["ManagedInspectResult"] = Object(("code", Integer()), ("descriptor", Ref("ManagedDescriptor"))),
        ["ManagedValidationResult"] = Object(("code", Integer()), ("errors", Array(new JsonObject { ["type"] = "object" }))),
        ["ManagedOperationResult"] = Object(("code", Integer()), ("replayed", Boolean()), ("receipt", new JsonObject { ["type"] = "object" })),
        ["ManagedCancellationResult"] = Object(("code", Integer()), ("state", Integer())),
        ["ConfigurationReadResult"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Core ConfigurationReadResult；秘密只以 SecretMetadata 出现，不包含值。",
            ["additionalProperties"] = true
        },
        ["ConfigurationSaveResult"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Core ConfigurationSaveResult；version/conflict/receipt 均由存储事务产生。",
            ["additionalProperties"] = true
        },
        ["ConfigurationFindResult"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
        ["ZbaTaskSubmission"] = RequiredObject(
            new[] { "sourceKey", "sessionId", "prompt", "target", "timeoutSeconds" },
            ("sourceKey", String()), ("sessionId", String()), ("prompt", String()),
            ("target", String()), ("timeoutSeconds", Integer())),
        ["ZbaTaskAccepted"] = Object(("id", String()), ("state", Integer()), ("acceptedAt", String())),
        ["ZbaTaskSummary"] = Object(("id", String()), ("state", Integer()), ("target", String()),
            ("sessionId", String()), ("createdAt", String()), ("updatedAt", String()),
            ("durationMs", Integer()), ("toolCalls", Integer()), ("errorCode", NullableString()),
            ("cancellationRequested", Boolean())),
        ["ZbaTaskPage"] = Object(("items", Array(Ref("ZbaTaskSummary"))), ("nextCursor", NullableString())),
        ["ZbaTaskCancelResult"] = Object(("state", Integer()), ("cancellationRequested", Boolean()), ("outcome", String())),
        ["ZbaApprovalSummary"] = Object(("id", String()), ("toolId", String()), ("summary", String()),
            ("session", String()), ("expiresAt", String()), ("policyVersion", Integer())),
        ["ZbaApprovalSummaryList"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = Ref("ZbaApprovalSummary")
        },
        ["ZbaApprovalDecision"] = RequiredObject(new[] { "approve" }, ("approve", Boolean())),
        ["ZbaApprovalDecisionResult"] = Object(("ok", Boolean()), ("decision", String()), ("reasonCode", String())),
        ["ZbaEvent"] = Object(("sequence", Integer()), ("type", String()), ("at", String()),
            ("taskId", NullableString()), ("state", NullableString()), ("version", NullableString()),
            ("reasonCode", NullableString())),
        ["ZbaEventPage"] = Object(("items", Array(Ref("ZbaEvent"))), ("nextSequence", Integer()),
            ("resyncRequired", Boolean()), ("generatedAt", String())),
        ["OpenApiDocument"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true }
    };

    private static JsonObject Object(params (string Name, JsonObject Schema)[] properties)
        => new()
        {
            ["type"] = "object",
            ["properties"] = Properties(properties)
        };

    private static JsonObject RequiredObject(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var schema = Object(properties);
        schema["required"] = new JsonArray(required.Select(name => (JsonNode)JsonValue.Create(name)!).ToArray());
        return schema;
    }

    private static JsonObject Properties(params (string Name, JsonObject Schema)[] properties)
    {
        var result = new JsonObject();
        foreach (var (name, schema) in properties) result[name] = schema;
        return result;
    }

    private static JsonObject String() => new() { ["type"] = "string" };

    private static JsonObject NullableString() => new() { ["type"] = new JsonArray("string", "null") };

    private static JsonObject Integer() => new() { ["type"] = "integer" };

    private static JsonObject Boolean() => new() { ["type"] = "boolean" };

    private static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex ParameterPattern();
}
