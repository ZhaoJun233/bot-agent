using System.Text.Json.Nodes;

namespace BotAgent.Domain.Model;

/// <summary>一次请求的**组装结果**（请求体 + 这一轮真带了几张图）。</summary>
public readonly record struct BuiltRequest(JsonObject Payload, int AttachedImages, List<long> AttachedImageIds);

/// <summary>一次发送的**结果**：拿到的 JSON，以及"是不是去掉图片后重试成功的"与故障转移跳数。</summary>
public readonly record struct SendOutcome(string Json, bool TextOnlyRetry, int FallbackHops = 0);

/// <summary>
/// 一次模型请求使用的 Provider 路由覆盖。API Key 只在内存中沿调用链传递，禁止持久化、日志或诊断输出。
/// </summary>
public sealed record ModelProviderRoute(
    string ProviderId,
    string BaseUrl,
    string ModelName,
    string ApiKey,
    ModelProtocol Protocol = ModelProtocol.OpenAiChat,
    ModelProtocolOptions? Options = null)
{
    // Keep the original four-field source deconstruction contract.
    public void Deconstruct(out string providerId, out string baseUrl, out string modelName, out string apiKey)
        => (providerId, baseUrl, modelName, apiKey) = (ProviderId, BaseUrl, ModelName, ApiKey);

    // Positional records otherwise print credentials in incidental diagnostics.
    public override string ToString() => $"ModelProviderRoute {{ Protocol = {Protocol} }}";
}

public enum ModelProtocol
{
    OpenAiChat, OpenAiResponses, AnthropicMessages, GeminiGenerateContent, AzureOpenAi, Ollama
}

public sealed record ModelProtocolOptions(
    string? AzureDeployment = null,
    string AzureApiVersion = "2024-10-21",
    string AnthropicVersion = "2023-06-01",
    int? TimeoutSeconds = null);
