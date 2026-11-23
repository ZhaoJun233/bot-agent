namespace BotAgent.Domain.Messaging;

/// <summary>
/// 平台入站文件/文档附件句柄（仅含元数据与平台拉取引用，不在收包循环同步下载字节）。
/// </summary>
public sealed record InboundFileAttachment(
    string FileName,
    string? DownloadUrl,
    string? PlatformFileId = null,
    long? SizeBytes = null,
    string? MimeHint = null)
{
    public override string ToString()
        => $"InboundFileAttachment(ext={System.IO.Path.GetExtension(FileName ?? string.Empty)}, size={SizeBytes})";
}
