using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.Domain.Mcp;

/// <summary>
/// MCP 工具参数的**有界** JSON Schema 校验（2020-12 的一个保守子集）。
///
/// 远端 schema 是不可信输入；这里只做“模型写错字段/类型/必填”的当场拦截，不冒充完整
/// JSON Schema 实现：`$ref` / `oneOf` / `anyOf` / `allOf` / `pattern` 等关键字**不执行**，
/// 远端服务器仍然必须自己校验。支持 object/array/string/number/integer/boolean/null、
/// required、properties、additionalProperties（只认 true/false）、enum、长度与数值上下界、
/// maxItems 与递归深度上限。无法证明安全时一律拒绝（fail-closed）。
/// </summary>
public static class McpSchemaValidator
{
    private const int MaxDepth = 6;
    private const int MaxProperties = 64;

    public static bool TryValidate(string? schemaJson, JsonObject? arguments, out string reasonCode)
    {
        reasonCode = "mcp_schema_invalid";
        if (string.IsNullOrWhiteSpace(schemaJson) || arguments is null)
        {
            return false;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(schemaJson);
        }
        catch
        {
            return false;
        }

        if (parsed is not JsonObject schema ||
            !string.Equals(Text(schema["type"]), "object", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (Encoding.UTF8.GetByteCount(arguments.ToJsonString()) > McpLimits.MaxArgumentsBytes)
            {
                reasonCode = "mcp_args_too_large";
                return false;
            }
        }
        catch
        {
            return false;
        }

        if (!Object(schema, arguments, 0, out reasonCode))
        {
            return false;
        }

        reasonCode = "ok";
        return true;
    }

    private static bool Object(JsonObject schema, JsonObject value, int depth, out string reason)
    {
        reason = "mcp_schema_invalid";
        if (depth > MaxDepth)
        {
            reason = "mcp_args_too_deep";
            return false;
        }

        var properties = schema["properties"] as JsonObject;
        if (properties is { Count: > MaxProperties })
        {
            reason = "mcp_schema_too_large";
            return false;
        }

        // 默认拒绝未知字段（比 JSON Schema 的默认 true 更严）：模型编出来的参数不能靠
        // “远端大概会忽略”混过去；确实需要开放字段的服务器必须显式写 true。
        var allowExtra = schema["additionalProperties"] switch
        {
            JsonValue boolean when boolean.TryGetValue<bool>(out var allowed) => allowed,
            JsonObject => true,
            _ => false,
        };

        if (!allowExtra && (properties is null
                ? value.Count > 0
                : value.Any(pair => !properties!.ContainsKey(pair.Key))))
        {
            reason = "mcp_unknown_field";
            return false;
        }

        if (schema["required"] is JsonArray required)
        {
            foreach (var item in required)
            {
                var name = Text(item);
                if (string.IsNullOrEmpty(name))
                {
                    reason = "mcp_schema_invalid";
                    return false;
                }

                if (!value.ContainsKey(name))
                {
                    reason = "mcp_missing_required";
                    return false;
                }
            }
        }

        if (properties is null)
        {
            return true;
        }

        foreach (var pair in properties)
        {
            if (!value.ContainsKey(pair.Key))
            {
                continue;
            }

            var actual = value[pair.Key];
            if (actual is null)
            {
                var declared = Text(pair.Value?["type"]);
                if (declared is not null && declared != "null")
                {
                    reason = "mcp_bad_type";
                    return false;
                }

                continue;
            }

            if (!Value(pair.Value, actual, depth + 1, out reason))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Value(JsonNode? schemaNode, JsonNode actual, int depth, out string reason)
    {
        reason = "mcp_bad_type";
        if (depth > MaxDepth)
        {
            reason = "mcp_args_too_deep";
            return false;
        }

        if (schemaNode is JsonValue boolean && boolean.TryGetValue<bool>(out var allowed))
        {
            // JSON Schema 的布尔形式：true = 任意值，false = 禁止。
            if (!allowed)
            {
                reason = "mcp_schema_rejects_value";
            }

            return allowed;
        }

        if (schemaNode is not JsonObject schema)
        {
            reason = "mcp_schema_invalid";
            return false;
        }

        if (schema["enum"] is JsonArray allowedValues && allowedValues.Count > 0)
        {
            var expected = actual.ToJsonString();
            if (!allowedValues.Any(item => string.Equals(item?.ToJsonString(), expected, StringComparison.Ordinal)))
            {
                reason = "mcp_bad_value";
                return false;
            }
        }

        var type = Text(schema["type"]);
        if (!string.IsNullOrEmpty(type) && !MatchesType(actual, type))
        {
            reason = "mcp_bad_type";
            return false;
        }

        if (actual is JsonObject obj && schema["type"] is null && schema["properties"] is not null)
        {
            return Object(schema, obj, depth, out reason);
        }

        if (type == "object" && actual is JsonObject child)
        {
            return Object(schema, child, depth, out reason);
        }

        if (type == "array" && actual is JsonArray array)
        {
            if (schema["maxItems"] is JsonValue maxNode && maxNode.TryGetValue<int>(out var maxItems) &&
                array.Count > maxItems)
            {
                reason = "mcp_bad_value";
                return false;
            }

            if (schema["items"] is { } itemSchema)
            {
                foreach (var item in array)
                {
                    if (item is null)
                    {
                        var declared = Text(itemSchema["type"]);
                        if (declared is not null && declared != "null")
                        {
                            reason = "mcp_bad_type";
                            return false;
                        }

                        continue;
                    }

                    if (!Value(itemSchema, item, depth + 1, out reason))
                    {
                        return false;
                    }
                }
            }
        }

        if (type == "string" && actual is JsonValue stringNode && stringNode.TryGetValue<string>(out var text))
        {
            if (schema["minLength"] is JsonValue minNode && minNode.TryGetValue<int>(out var minLength) &&
                text.Length < minLength)
            {
                reason = "mcp_bad_length";
                return false;
            }

            if (schema["maxLength"] is JsonValue maxNode && maxNode.TryGetValue<int>(out var maxLength) &&
                text.Length > maxLength)
            {
                reason = "mcp_bad_length";
                return false;
            }
        }

        if (type is "number" or "integer" && actual is JsonValue numberNode)
        {
            if (schema["minimum"] is JsonValue minNode && minNode.TryGetValue<double>(out var minimum) &&
                !NumberAtLeast(numberNode, minimum))
            {
                reason = "mcp_bad_value";
                return false;
            }

            if (schema["maximum"] is JsonValue maxNode && maxNode.TryGetValue<double>(out var maximum) &&
                !NumberAtMost(numberNode, maximum))
            {
                reason = "mcp_bad_value";
                return false;
            }
        }

        reason = "ok";
        return true;
    }

    private static bool MatchesType(JsonNode node, string type) => type switch
    {
        "object" => node is JsonObject,
        "array" => node is JsonArray,
        "string" => node is JsonValue value && value.TryGetValue<string>(out _),
        "boolean" => node is JsonValue value && value.TryGetValue<bool>(out _),
        "integer" => node is JsonValue value && value.TryGetValue<long>(out _),
        "number" => node is JsonValue value && value.TryGetValue<double>(out _),
        "null" => node is null,
        _ => false,
    };

    private static bool NumberAtLeast(JsonValue node, double minimum)
        => node.TryGetValue<double>(out var value) && value >= minimum;

    private static bool NumberAtMost(JsonValue node, double maximum)
        => node.TryGetValue<double>(out var value) && value <= maximum;

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
