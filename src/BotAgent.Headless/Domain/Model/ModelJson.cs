using System.Text.Json;

namespace BotAgent.Domain.Model;

/// <summary>
/// 模型回包的**形状判定**（纯函数）。它原来挂在传输层上（<c>ModelTransport.HasChoices</c>），
/// 而客户端（<c>OpenAiClient</c>）也要用它 —— 静态方法过不了端口，于是按"纯函数下沉"的老规矩搬到 Domain。
/// </summary>
public static class ModelJson
{
    /// <summary>回包里有没有 <c>choices</c>（空数组也算"没有"——上游把额度/风控错误塞进 200 里就是这样）。</summary>
    public static bool HasChoices(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("choices", out var choices) &&
                   choices.ValueKind == JsonValueKind.Array &&
                   choices.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
