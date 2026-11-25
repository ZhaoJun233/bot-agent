using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Documents;

/// <summary>文档解析包运行期参数快照。</summary>
public sealed record DocumentOptions(
    int MaxFileBytes = 10 * 1024 * 1024,
    int MaxInlineChars = 6000,
    bool AutoExtractInbound = true);

/// <summary>宿主侧受控文档字节拉取结果（已过 SSRF 与大小限制）。</summary>
public sealed record DocumentFetchResult(
    bool Ok,
    byte[] Data,
    string FileName,
    string? MimeType = null,
    string ReasonCode = "ok")
{
    public override string ToString() => $"DocumentFetchResult(ok={Ok}, bytes={Data?.Length ?? 0}, reason={ReasonCode})";
}

/// <summary>纯托管文档文本提取产物。</summary>
public sealed record DocumentExtractResult(
    bool Ok,
    string Text,
    int TotalUnits,
    bool Truncated,
    string Format,
    string ReasonCode = "ok")
{
    public override string ToString()
        => $"DocumentExtractResult(ok={Ok}, fmt={Format}, units={TotalUnits}, chars={Text?.Length ?? 0}, reason={ReasonCode})";
}

/// <summary>宿主为 <c>preset.feature.documents</c> 提供的受控网络与会话入站暂存端口。</summary>
public interface IDocumentHostPorts
{
    DocumentOptions ReadOptions();
    void StageInboundDocument(string sourceKey, string fileName, byte[] data, string? mimeType = null);
    Task<DocumentFetchResult> FetchAsync(string sourceKey, string sourceOrUrl, CancellationToken ct);
}

/// <summary>文档解析包向宿主暴露的纯解析与预览能力贡献。</summary>
public interface IDocumentContribution : IPluginContribution
{
    bool IsReady { get; }
    DocumentExtractResult ExtractBytes(string fileName, ReadOnlyMemory<byte> data, string? section = null, int? maxChars = null);
    Task<DocumentExtractResult> ExtractInboundAsync(string sourceKey, string fileName, byte[] data, string? mimeType, CancellationToken ct);
}
