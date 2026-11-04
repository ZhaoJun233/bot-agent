using System.Text;
using System.Text.Json.Nodes;
namespace BotAgent.Model.Protocols;

internal static class AnthropicProtocol
{
    internal static JsonObject Request(JsonObject chat, bool promptCache = false, int? stableSystemPrefixLength = null)
    {
        var messages = new JsonArray(); var system = new JsonArray();
        var sawFirstSystem = false;
        var stableBreakpoint = false;
        var boundaryRejected = false;
        foreach (var m in ProtocolJson.Array(chat["messages"]).OfType<JsonObject>())
        {
            var role = ProtocolJson.Text(m["role"]); var parts = new JsonArray();
            if (role is "system" or "developer")
            {
                var sourceParts = ProtocolJson.Parts(m["content"]);
                // 只有「首条 system/developer 消息 + 缓存开启 + 传了边界」才尝试拆分稳定前缀；
                // 其余 system 消息按原样追加（顺序不变），旧形状完全保留。
                if (sawFirstSystem || stableSystemPrefixLength is null || !promptCache)
                {
                    foreach (var p in sourceParts) system.Add(p?.DeepClone());
                    sawFirstSystem = true;
                    continue;
                }
                sawFirstSystem = true;
                if (TrySplitStableSystem(sourceParts, stableSystemPrefixLength.Value, out var stableBlock, out var dynamicBlock))
                {
                    system.Add(stableBlock);
                    if (dynamicBlock is not null) system.Add(dynamicBlock);
                    stableBreakpoint = true;
                }
                else
                {
                    // 边界非法或首 system 不是简单文本：fail closed，不拆分也不猜内容。
                    boundaryRejected = true;
                    foreach (var p in sourceParts) system.Add(p?.DeepClone());
                }
                continue;
            }
            if (role == "tool") parts.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m["tool_call_id"]?.DeepClone(), ["content"] = m["content"]?.DeepClone() });
            else foreach (var p in ProtocolJson.Parts(m["content"]).OfType<JsonObject>())
            {
                if (ProtocolJson.Text(p["type"]) == "text") { if (ProtocolJson.Text(p["text"]).Length > 0) parts.Add(p.DeepClone()); }
                else if (ProtocolJson.Text(p["type"]) == "image_url")
                {
                    var url = ProtocolJson.Text(p["image_url"]?["url"]); JsonObject source;
                    if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { var (mime, data) = ProtocolJson.DataImage(url); source = new() { ["type"] = "base64", ["media_type"] = mime, ["data"] = data }; }
                    else source = new() { ["type"] = "url", ["url"] = url };
                    parts.Add(new JsonObject { ["type"] = "image", ["source"] = source });
                }
                else throw new ModelFault("model_content_unsupported");
            }
            foreach (var call in ProtocolJson.Array(m["tool_calls"]).OfType<JsonObject>()) parts.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call["id"]?.DeepClone(), ["name"] = call["function"]?["name"]?.DeepClone(), ["input"] = ProtocolJson.Arguments(call["function"]?["arguments"]) });
            var mappedRole = role == "assistant" ? "assistant" : "user";
            if (messages.LastOrDefault() is JsonObject last && ProtocolJson.Text(last["role"]) == mappedRole)
                foreach (var part in parts) ((JsonArray)last["content"]!).Add(part?.DeepClone());
            else messages.Add(new JsonObject { ["role"] = mappedRole, ["content"] = parts });
        }
        // 缓存断点：有合法首 system 边界时只标稳定前缀块（动态 system 与最后一条消息都不标）；
        // 无边界时退回旧形状（system 末尾 + 最后一条消息最后一个 content 块）；
        // 传了边界却无法安全拆分时一个断点都不加（fail closed，不猜内容）。
        if (promptCache && !boundaryRejected && !stableBreakpoint && stableSystemPrefixLength is null)
        {
            Ephemeral(system.Count > 0 ? system[^1] : null);
            if (messages.Count > 0 && messages[^1]?["content"] is JsonArray lastContent && lastContent.Count > 0) Ephemeral(lastContent[^1]);
        }
        var result = new JsonObject { ["messages"] = messages, ["max_tokens"] = chat["max_tokens"]?.DeepClone() ?? JsonValue.Create(2048), ["stream"] = false };
        if (system.Count > 0) result["system"] = system;
        ProtocolJson.Copy(chat, result, "model");
        // Messages models may reject temperature and top_p together; temperature is the primary control.
        ProtocolJson.Copy(chat, result, chat["temperature"] is null ? "top_p" : "temperature");
        if (result["temperature"] is JsonValue temperature && temperature.TryGetValue<double>(out var t))
            result["temperature"] = Math.Clamp(t, 0, 1);
        if (chat["tools"] is JsonArray tools) result["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject { ["name"] = t?["function"]?["name"]?.DeepClone(), ["description"] = t?["function"]?["description"]?.DeepClone(), ["input_schema"] = t?["function"]?["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" } }).ToArray());
        if (chat["tool_choice"] is JsonObject choice) result["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = choice["function"]?["name"]?.DeepClone() };
        else if (chat["tool_choice"] is { } mode) result["tool_choice"] = new JsonObject { ["type"] = ProtocolJson.Text(mode) == "required" ? "any" : ProtocolJson.Text(mode) };
        return result;
    }
    /// <summary>在 content block 上标记一个短暂缓存断点（Anthropic 只认这个形状）。</summary>
    private static void Ephemeral(JsonNode? block)
    {
        if (block is JsonObject part) part["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
    }
    /// <summary>
    /// 只有首条 system/developer 消息是「简单文本」（恰好一个 text part，且只带 type/text）时才允许拆分。
    /// 边界按 UTF-16 字符数计；&lt;=0 或大于文本长度视为非法，返回 false 让调用方 fail closed。
    /// </summary>
    private static bool TrySplitStableSystem(JsonArray parts, int boundary, out JsonObject stable, out JsonObject? dynamic)
    {
        stable = null!;
        dynamic = null;
        if (parts.Count != 1 || parts[0] is not JsonObject part) return false;
        if (ProtocolJson.Text(part["type"]) != "text") return false;
        if (part["text"] is not JsonValue textValue || !textValue.TryGetValue<string>(out var text)) return false;
        // 带其它字段（例如已有 cache_control）就不是简单文本，拒绝猜测。
        if (part.Count != 2) return false;
        if (boundary <= 0 || boundary > text.Length) return false;
        stable = new JsonObject
        {
            ["type"] = "text",
            ["text"] = text[..boundary],
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
        };
        if (boundary < text.Length) dynamic = new JsonObject { ["type"] = "text", ["text"] = text[boundary..] };
        return true;
    }

    internal static JsonObject Response(JsonObject root)
    {
        if (root["content"] is not JsonArray content) throw new ModelFault("model_malformed_response");
        var text = new StringBuilder(); var calls = new JsonArray();
        foreach (var part in ProtocolJson.Objects(content))
        {
            if (ProtocolJson.Text(part["type"]) == "text") text.Append(ProtocolJson.Text(part["text"]));
            if (ProtocolJson.Text(part["type"]) == "tool_use") calls.Add(ProtocolJson.Call(ProtocolJson.Text(part["id"]), ProtocolJson.Text(part["name"]), part["input"]));
        }
        if (content.Count == 0) return new JsonObject { ["choices"] = new JsonArray() };
        var usage = root["usage"] as JsonObject;
        return ProtocolJson.Envelope(text.ToString(), calls, usage is null ? null : ProtocolJson.Usage(usage, "input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens"), ProtocolJson.Text(root["stop_reason"]) == "max_tokens" ? "length" : null);
    }
}
