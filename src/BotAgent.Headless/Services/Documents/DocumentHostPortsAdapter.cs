using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BotAgent.Domain.Memory;
using BotAgent.Services.Documents;
using BotAgent.Services.Memory;
using BotAgent.Services.Net;

namespace BotAgent.Services.Documents;

/// <summary>
/// 宿主侧受控文档下载与会话入站暂存适配器：
/// 1) 强制走独立 SSRF-safe <see cref="IHttpFetcher"/>（逐跳校验重定向目标，拦截内网/回环 IP）；
/// 2) 流式读取响应体，超过上限（硬顶棚 10 MiB）立即中止流，绝不把大文件读进内存；
/// 3) 按 <c>sourceKey</c> 隔离暂存最近上传的入站附件，跨会话绝不可见。
/// </summary>
public sealed class DocumentHostPortsAdapter(
    IHttpFetcher http,
    Func<bool> allowPrivateOutbound,
    Func<IReadOnlyDictionary<string, JsonElement>?> readSettings,
    Action<string> log,
    Plugins.PluginManager? plugins = null) : IDocumentHostPorts, IInboundDocumentProcessor
{
    public const int HardMaxBytes = 10 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private const int MaxStagedConversations = 64;
    private const int MaxStagedPerConversation = 4;
    private static readonly TimeSpan StagedTtl = TimeSpan.FromMinutes(30);

    private sealed record StagedDocument(string FileName, byte[] Data, string? MimeType, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, List<StagedDocument>> _staged = new(StringComparer.Ordinal);

    public bool IsAutoExtractEnabled => ReadOptions().AutoExtractInbound;

    public DocumentOptions ReadOptions()
    {
        var values = readSettings();
        if (values is null) return new DocumentOptions();
        var auto = ReadBool(values, "autoExtractInbound", true);
        var maxBytes = Math.Clamp(ReadInt(values, "maxFileBytes", HardMaxBytes), 65536, HardMaxBytes);
        var maxChars = Math.Clamp(ReadInt(values, "maxInlineChars", 6000), 500, 12000);
        return new DocumentOptions(maxBytes, maxChars, auto);
    }

    public async Task<string?> ProcessInboundAsync(
        string sourceKey,
        Domain.Messaging.InboundFileAttachment attachment,
        byte[]? resolvedData,
        string? resolvedUrl,
        CancellationToken ct = default)
    {
        var options = ReadOptions();
        var maxBytes = Math.Clamp(options.MaxFileBytes, 65536, HardMaxBytes);
        if (attachment.SizeBytes is { } declared && declared > maxBytes)
        {
            log($"[Documents] inbound skipped reason=declared_too_large size={declared}");
            return null;
        }

        var fileName = string.IsNullOrWhiteSpace(attachment.FileName) ? "attachment.dat" : Path.GetFileName(attachment.FileName.Trim());
        var data = resolvedData;
        if (data is null && !string.IsNullOrWhiteSpace(resolvedUrl))
        {
            var fetched = await DownloadBoundedAsync(resolvedUrl!, maxBytes, ct).ConfigureAwait(false);
            if (!fetched.Ok)
            {
                log($"[Documents] inbound download failed reason={fetched.ReasonCode}");
                return null;
            }
            data = fetched.Data;
            if (!string.IsNullOrWhiteSpace(fetched.FileName) && !Path.HasExtension(fileName))
                fileName = fetched.FileName;
        }

        if (data is not { Length: > 0 } || data.Length > maxBytes)
        {
            log($"[Documents] inbound skipped reason=size_or_empty bytes={data?.Length ?? 0}");
            return null;
        }

        StageInboundDocument(sourceKey, fileName, data, attachment.MimeHint);
        if (!options.AutoExtractInbound || plugins is null)
            return null;

        try
        {
            if (!plugins.TryGetContribution<IDocumentContribution>("preset.feature.documents", out var contribution)
                || contribution is null)
                return null;

            var extracted = await contribution.ExtractInboundAsync(sourceKey, fileName, data, attachment.MimeHint, ct).ConfigureAwait(false);
            log($"[Documents] inbound extract ext={Path.GetExtension(fileName)} bytes={data.Length} ok={extracted.Ok} reason={extracted.ReasonCode}");
            var safeTitle = Domain.Conversation.MessageMarkers.EscapeExternalControlTags(fileName);
            if (!extracted.Ok || string.IsNullOrWhiteSpace(extracted.Text))
            {
                return extracted.ReasonCode switch
                {
                    "pdf_no_text_layer" => $"〔文件提示：{safeTitle} 是扫描版 PDF，未检测到可提取文字层〕",
                    "pdf_encrypted" => $"〔文件提示：{safeTitle} 已加密，无法自动提取文本〕",
                    _ => null
                };
            }

            var safeBody = Domain.Conversation.MessageMarkers.EscapeExternalControlTags(extracted.Text);
            return $"[文件附件（外部不可信内容）：{safeTitle} | {extracted.Format} | 共{extracted.TotalUnits}节]\n{safeBody}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log($"[Documents] inbound extract exception type={ex.GetType().Name}");
            return null;
        }
    }

    public void EvictConversation(string sourceKey)
    {
        if (!string.IsNullOrWhiteSpace(sourceKey))
            _staged.TryRemove(sourceKey, out _);
    }

    public void StageInboundDocument(string sourceKey, string fileName, byte[] data, string? mimeType = null)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || data is not { Length: > 0 } || data.Length > HardMaxBytes)
            return;
        var now = Clock.UtcNow;
        if (_staged.Count >= MaxStagedConversations && !_staged.ContainsKey(sourceKey))
        {
            foreach (var old in _staged.Keys.Take(16))
                _staged.TryRemove(old, out _);
        }
        var safeName = string.IsNullOrWhiteSpace(fileName) ? "attachment.dat" : Path.GetFileName(fileName.Trim());
        var list = _staged.GetOrAdd(sourceKey, _ => new List<StagedDocument>());
        lock (list)
        {
            list.RemoveAll(d => d.ExpiresAt <= now || string.Equals(d.FileName, safeName, StringComparison.OrdinalIgnoreCase));
            list.Add(new StagedDocument(safeName, data, mimeType, now.Add(StagedTtl)));
            while (list.Count > MaxStagedPerConversation)
                list.RemoveAt(0);
        }
    }

    public async Task<DocumentFetchResult> FetchAsync(string sourceKey, string sourceOrUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceOrUrl))
            return new(false, [], string.Empty, null, "empty_source");

        var trimmed = sourceOrUrl.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Contains("://", StringComparison.Ordinal))
                return new(false, [], string.Empty, null, "unsupported_url_scheme");
            if (MemoryUseScope.HasMemory)
            {
                try { return MemoryUseScope.Use(() => TryReadStaged(sourceKey, trimmed)); }
                catch (MemoryUseInvalidatedException) { return new(false, [], string.Empty, null, "memory_taint_egress_denied"); }
            }
            return TryReadStaged(sourceKey, trimmed);
        }

        if (MemoryUseScope.HasMemory && !MemoryUseScope.AllowsEgress(ToolEgressClass.ExternalReadEgress))
            return new(false, [], string.Empty, null, "memory_taint_egress_denied");

        var options = ReadOptions();
        var maxBytes = Math.Clamp(options.MaxFileBytes, 65536, HardMaxBytes);
        return await DownloadBoundedAsync(trimmed, maxBytes, ct).ConfigureAwait(false);
    }

    private DocumentFetchResult TryReadStaged(string sourceKey, string selector)
    {
        if (!_staged.TryGetValue(sourceKey, out var list))
            return new(false, [], string.Empty, null, "staged_document_not_found");
        var now = Clock.UtcNow;
        lock (list)
        {
            list.RemoveAll(d => d.ExpiresAt <= now);
            if (list.Count == 0)
                return new(false, [], string.Empty, null, "staged_document_not_found");
            var match = string.Equals(selector, "latest", StringComparison.OrdinalIgnoreCase)
                ? list[^1]
                : list.LastOrDefault(d => string.Equals(d.FileName, selector, StringComparison.OrdinalIgnoreCase)
                    || d.FileName.Contains(selector, StringComparison.OrdinalIgnoreCase));
            return match is null
                ? new(false, [], string.Empty, null, "staged_document_not_found")
                : new(true, match.Data, match.FileName, match.MimeType);
        }
    }

    private async Task<DocumentFetchResult> DownloadBoundedAsync(string rawUrl, int maxBytes, CancellationToken ct)
    {
        if (MemoryUseScope.HasMemory && !MemoryUseScope.AllowsEgress(ToolEgressClass.ExternalReadEgress))
            return new(false, [], string.Empty, null, "memory_taint_egress_denied");

        var allowPrivate = allowPrivateOutbound();
        if (!SafeUrl.TryValidate(rawUrl, allowPrivate, out var currentUri, out var ssrfWhy))
        {
            log($"[Documents] blocked outbound url reason={ssrfWhy}");
            return new(false, [], string.Empty, null, "ssrf_blocked");
        }

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
                using var resp = await (MemoryUseScope.HasMemory
                    ? MemoryUseScope.Use(() => http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                    : http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)).ConfigureAwait(false);
                if (IsRedirect(resp.StatusCode) && resp.Headers.Location is { } location)
                {
                    if (hop == MaxRedirects)
                        return new(false, [], string.Empty, null, "too_many_redirects");
                    var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    if (!SafeUrl.TryValidate(nextUri.AbsoluteUri, allowPrivate, out var safeNext, out _))
                        return new(false, [], string.Empty, null, "ssrf_redirect_blocked");
                    currentUri = safeNext;
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                    return new(false, [], string.Empty, null, $"http_{(int)resp.StatusCode}");

                if (resp.Content.Headers.ContentLength is { } contentLen && contentLen > maxBytes)
                    return new(false, [], string.Empty, null, "file_too_large");

                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                        return new(false, [], string.Empty, null, "file_too_large");
                    output.Write(buffer, 0, read);
                }

                var mime = resp.Content.Headers.ContentType?.MediaType;
                var fileName = InferFileName(currentUri, resp.Content.Headers.ContentDisposition?.FileNameStar
                    ?? resp.Content.Headers.ContentDisposition?.FileName, mime);
                return new(true, output.ToArray(), fileName, mime);
            }
            return new(false, [], string.Empty, null, "too_many_redirects");
        }
        catch (MemoryUseInvalidatedException)
        {
            return new(false, [], string.Empty, null, "memory_taint_egress_denied");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.Message.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("private", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("loopback", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, [], string.Empty, null, "ssrf_blocked");
        }
        catch (Exception ex)
        {
            log($"[Documents] download failed type={ex.GetType().Name}");
            return new(false, [], string.Empty, null, "download_failed");
        }
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string InferFileName(Uri uri, string? dispositionName, string? mime)
    {
        var candidate = (dispositionName ?? string.Empty).Trim('"', '\'', ' ');
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(candidate) || !Path.HasExtension(candidate))
        {
            var ext = mime?.ToLowerInvariant() switch
            {
                "application/pdf" => ".pdf",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
                "text/html" => ".html",
                "application/json" => ".json",
                "text/csv" => ".csv",
                _ => ".txt"
            };
            candidate = (string.IsNullOrWhiteSpace(candidate) ? "document" : candidate) + ext;
        }
        return candidate;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, JsonElement> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b : fallback,
            _ => fallback
        };
    }

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return fallback;
    }
}
