using System.Text;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Identity;
using BotAgent.Domain.Model;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Scheduler;
using BotAgent.Panel;
using BotAgent.Services.Documents;
using BotAgent.Services.Plugins;

namespace BotAgent.Services.Workspace;

/// <summary>
/// 个人工作台服务（严格隔离在 <c>workspace:owner</c>）：
/// - 绝不读取、检索或列出任何 QQ/飞书群聊或私聊会话；
/// - 支持纯文本对话（直连 <see cref="IModelClient"/>）、后台任务（经共享 <c>AgentTaskRuntime</c>）以及 ≤10 MiB 文档附件解析注入；
/// - 写消息后仅通过 <c>_onAppended</c> 发布无正文增量事件 <c>workspace.message.appended</c>。
/// </summary>
public sealed class WorkspaceService : IDisposable
{
    public const int MaxAttachmentBytes = 10 * 1024 * 1024;
    private const int MaxBase64Chars = ((MaxAttachmentBytes + 2) / 3) * 4 + 64;
    private const int MaxPromptChars = 8000;
    private const int MaxExtractedContextChars = 6000;

    private readonly IWorkspaceConversationStore _store;
    private readonly IModelClient? _brain;
    private readonly PluginManager? _plugins;
    private readonly IIdentityLinkResolver? _identityLinks;
    private readonly Func<string, string, string, CancellationToken, ValueTask<string?>>? _submitTask;
    private readonly Action<string>? _onAppended;
    private readonly Action<string>? _onClearDocuments;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<bool>? _isAiEnabled;
    private readonly ITokenUsageRecorder? _usage;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _disposed;

    public WorkspaceService(
        IWorkspaceConversationStore store,
        IModelClient? brain = null,
        PluginManager? plugins = null,
        IIdentityLinkResolver? identityLinks = null,
        Func<string, string, string, CancellationToken, ValueTask<string?>>? submitTask = null,
        Action<string>? onAppended = null,
        Func<DateTimeOffset>? utcNow = null,
        Action<string>? onClearDocuments = null,
        Func<bool>? isAiEnabled = null,
        ITokenUsageRecorder? usage = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _brain = brain;
        _plugins = plugins;
        _identityLinks = identityLinks;
        _submitTask = submitTask;
        _onAppended = onAppended;
        _onClearDocuments = onClearDocuments;
        _utcNow = utcNow ?? (() => Clock.UtcNow);
        _isAiEnabled = isAiEnabled;
        _usage = usage;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _sendGate.Dispose();
    }

    public ZbaWorkspaceMessagesResponse ReadMessages(long afterSequence = 0, int limit = 100)
    {
        var snap = _store.ReadAfter(afterSequence, limit);
        var views = snap.Messages
            .Select(m => new ZbaWorkspaceMessageView(
                m.Sequence, m.Role, m.Text, m.Mode, m.AttachmentName, m.CreatedUnix, m.TaskId, m.Status))
            .ToArray();
        return new ZbaWorkspaceMessagesResponse(SchedulerLimits.WorkspaceOwnerScopeKey, snap.LastSequence, views);
    }

    public async ValueTask<ZbaWorkspaceSendResult> SendAsync(ZbaWorkspaceSendRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = (request.IdempotencyKey ?? string.Empty).Trim();
        if (!IsValidIdempotencyKey(key))
            return new(false, false, request.IdempotencyKey ?? string.Empty, "invalid_idempotency_key", null, ReadMessages());

        var prompt = (request.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(request.AttachmentBase64))
            return new(false, false, key, "workspace_text_required", null, ReadMessages());
        if (prompt.Length > MaxPromptChars)
            return new(false, false, key, "workspace_text_too_long", null, ReadMessages());

        if (_isAiEnabled is not null && !_isAiEnabled())
            return new(false, false, key, "ai_mode_disabled", null, ReadMessages());
        if (_plugins is not null
            && _plugins.TryGetContribution<ITenantQuotaManagement>("preset.feature.token-quota", out var quotaMgmt)
            && quotaMgmt is not null
            && quotaMgmt.GetQuota(SchedulerLimits.WorkspaceOwnerScopeKey).EnergySaving)
        {
            return new(false, false, key, "quota_exceeded", null, ReadMessages());
        }

        var mode = string.Equals(request.Mode, "task", StringComparison.OrdinalIgnoreCase) ? "task" : "chat";
        string? attachmentSummary = null;
        string? safeFileName = null;

        if (!string.IsNullOrWhiteSpace(request.AttachmentBase64))
        {
            var trimmedB64 = request.AttachmentBase64.Trim();
            if (trimmedB64.Length > MaxBase64Chars)
                return new(false, false, key, "attachment_too_large", null, ReadMessages());

            byte[] rawBytes;
            try
            {
                rawBytes = Convert.FromBase64String(trimmedB64);
            }
            catch (FormatException)
            {
                return new(false, false, key, "attachment_invalid_base64", null, ReadMessages());
            }
            if (rawBytes.Length == 0 || rawBytes.Length > MaxAttachmentBytes)
                return new(false, false, key, "attachment_too_large", null, ReadMessages());

            safeFileName = SanitizeFileName(request.AttachmentName);
            if (HasDangerousBinarySignature(safeFileName, rawBytes))
                return new(false, false, key, "executable_rejected", null, ReadMessages());

            var (summary, extractErr) = await ExtractAttachmentTextAsync(safeFileName, rawBytes, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(extractErr))
                return new(false, false, key, extractErr, null, ReadMessages());
            attachmentSummary = summary;
        }

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_store.TryFindByIdempotencyKey(key, out var replayed))
            {
                return new(true, true, key, "replayed", replayed?.TaskId, ReadMessages());
            }

            var now = _utcNow();
            var nowUnix = now.ToUnixTimeSeconds();
            var userDisplay = string.IsNullOrWhiteSpace(prompt) ? $"[上传文档：{safeFileName}]" : prompt;

            if (mode == "task")
            {
                if (_submitTask is null)
                    return new(false, false, key, "task_runtime_unavailable", null, ReadMessages());

                var effectivePrompt = string.IsNullOrWhiteSpace(attachmentSummary)
                    ? prompt
                    : $"{prompt}\n\n{attachmentSummary}".Trim();
                string? taskId;
                try
                {
                    taskId = await _submitTask(key + ":task", effectivePrompt, SchedulerLimits.WorkspaceOwnerScopeKey, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (ZbaBackendException ex)
                {
                    return new(false, false, key, ex.Code, null, ReadMessages());
                }
                catch
                {
                    return new(false, false, key, "task_submit_failed", null, ReadMessages());
                }

                if (string.IsNullOrWhiteSpace(taskId))
                    return new(false, false, key, "task_submit_failed", null, ReadMessages());

                _store.Append("user", userDisplay, mode, nowUnix, idempotencyKey: key, attachmentName: safeFileName, taskId: taskId, status: "accepted");
                _store.Append("assistant", $"已提交个人工作台任务（{taskId}），请在右侧任务栏跟踪进度与审批。", "task",
                    _utcNow().ToUnixTimeSeconds(), taskId: taskId, status: "running");
                _onAppended?.Invoke("task");
                return new(true, false, key, null, taskId, ReadMessages());
            }

            if (_brain is null)
                return new(false, false, key, "model_unavailable", null, ReadMessages());

            var contextSnap = _store.ReadAfter(0, 19);
            var messages = contextSnap.Messages
                .Select(m => new ChatMessage
                {
                    Role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? MessageRole.Self : MessageRole.Peer,
                    SenderName = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "BotAgent" : "Owner",
                    Text = m.Text,
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(m.CreatedUnix)
                })
                .ToList();
            messages.Add(new ChatMessage
            {
                Role = MessageRole.Peer,
                SenderName = "Owner",
                Text = string.IsNullOrWhiteSpace(attachmentSummary) ? userDisplay : $"{userDisplay}\n\n{attachmentSummary}",
                Timestamp = now
            });

            CompletionResult completion;
            try
            {
                completion = await _brain.CompleteAsync(
                    messages,
                    profilesText: "[个人工作台对话] 当前处于本人 Web 专属工作台（workspace:owner），严禁访问或推测任何外部群聊消息。",
                    ct: ct,
                    isPrivateConversation: true,
                    promptCacheScope: SchedulerLimits.WorkspaceOwnerScopeKey,
                    promptOverrides: new ConversationPromptOverrides(PlatformDisplayName: "个人工作台", PlatformNeutral: true)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new(false, false, key, "model_call_failed", null, ReadMessages());
            }

            if (completion.PromptTokens > 0 || completion.CompletionTokens > 0)
                _usage?.RecordUsage(SchedulerLimits.WorkspaceOwnerScopeKey, completion.PromptTokens, completion.CompletionTokens);

            var reply = string.IsNullOrWhiteSpace(completion.Reply) ? "（已完成处理）" : completion.Reply.Trim();
            _store.Append("user", userDisplay, mode, nowUnix, idempotencyKey: key, attachmentName: safeFileName, status: "accepted");
            _store.Append("assistant", reply, "chat", _utcNow().ToUnixTimeSeconds(), status: "completed");
            _onAppended?.Invoke("assistant");
            return new(true, false, key, null, null, ReadMessages());
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public ZbaWorkspaceClearResult ClearMessages()
    {
        _store.Clear(_utcNow().ToUnixTimeSeconds());
        _onClearDocuments?.Invoke(SchedulerLimits.WorkspaceOwnerScopeKey);
        _onAppended?.Invoke("cleared");
        return new ZbaWorkspaceClearResult(true, ReadMessages());
    }

    public ZbaWorkspaceIdentityResponse ReadIdentity()
    {
        if (_identityLinks is null)
            return new(false, false, false, false, Array.Empty<ZbaWorkspaceIdentityLinkView>(), null);
        var st = _identityLinks.GetStatus();
        var links = st.Links
            .Select(l => new ZbaWorkspaceIdentityLinkView(l.CanonicalPersonId, l.PlatformId, l.AccountScope, l.InstanceId, l.MaskedUserId, l.LinkedUnix))
            .ToArray();
        return new(st.Enabled, st.Preferences.ShareTodos, st.Preferences.ShareKnowledge, st.Preferences.SharePreferences, links, st.WorkspaceCanonicalPersonId);
    }

    public ZbaWorkspaceIdentityResponse ToggleIdentity(ZbaWorkspaceIdentityToggleRequest request)
    {
        var (response, _) = ToggleIdentityChecked(request);
        return response;
    }

    public (ZbaWorkspaceIdentityResponse Response, string? ErrorCode) ToggleIdentityChecked(ZbaWorkspaceIdentityToggleRequest request)
    {
        if (_identityLinks is null)
            return (new(false, false, false, false, Array.Empty<ZbaWorkspaceIdentityLinkView>(), null), null);
        _identityLinks.SetEnabled(request.Enabled);
        _identityLinks.UpdateSharePreferences(new IdentitySharePreferences(
            request.ShareTodos, request.ShareKnowledge, request.SharePreferences));
        if (!request.Enabled)
        {
            _identityLinks.BindWorkspace(null);
        }
        else if (request.WorkspaceCanonicalPersonId is not null)
        {
            var bind = _identityLinks.BindWorkspace(request.WorkspaceCanonicalPersonId);
            if (!bind.Ok)
                return (ReadIdentity(), bind.ReasonCode);
        }
        return (ReadIdentity(), null);
    }

    private async Task<(string? Summary, string? ErrorCode)> ExtractAttachmentTextAsync(string fileName, byte[] data, CancellationToken ct)
    {
        if (_plugins is not null
            && _plugins.TryGetContribution<IDocumentContribution>("preset.feature.documents", out var contribution)
            && contribution is not null)
        {
            var extracted = await contribution.ExtractInboundAsync(SchedulerLimits.WorkspaceOwnerScopeKey, fileName, data, null, ct).ConfigureAwait(false);
            if (!extracted.Ok)
            {
                if (extracted.ReasonCode is "executable_rejected" or "zip_bomb_rejected" or "file_too_large")
                    return (null, extracted.ReasonCode);
            }
            else if (!string.IsNullOrWhiteSpace(extracted.Text))
            {
                var bounded = extracted.Text.Length > MaxExtractedContextChars
                    ? extracted.Text[..MaxExtractedContextChars]
                    : extracted.Text;
                var safeName = MessageMarkers.EscapeExternalControlTags(fileName);
                var safeBody = MessageMarkers.EscapeExternalControlTags(bounded);
                return ($"[个人工作台附件（外部不可信内容）：{safeName}]\n{safeBody}", null);
            }
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".txt" or ".md" or ".json" or ".csv" or ".log")
        {
            var text = Encoding.UTF8.GetString(data);
            if (text.Length > MaxExtractedContextChars) text = text[..MaxExtractedContextChars];
            return ($"[个人工作台附件（外部不可信内容）：{MessageMarkers.EscapeExternalControlTags(fileName)}]\n{MessageMarkers.EscapeExternalControlTags(text)}", null);
        }

        return ($"〔附件提示：{MessageMarkers.EscapeExternalControlTags(fileName)}（{data.Length} 字节）未检测到可提取文字〕", null);
    }

    private static bool IsValidIdempotencyKey(string key)
        => key.Length is >= 8 and <= 128
            && key.All(c => c is >= (char)0x21 and <= (char)0x7E);

    private static string SanitizeFileName(string? rawName)
    {
        var normalized = (rawName ?? "document.txt").Replace('\\', '/').Trim();
        var file = Path.GetFileName(normalized);
        var clean = new string(file.Where(c => !char.IsControl(c) && c is not ('/' or '\\')).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean)) return "document.txt";
        return clean.Length > 120 ? clean[..120] : clean;
    }

    private static bool HasDangerousBinarySignature(string fileName, byte[] rawBytes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".exe" or ".dll" or ".bat" or ".cmd" or ".ps1" or ".sh" or ".msi" or ".so" or ".dylib")
            return true;
        if (rawBytes.Length >= 2 && rawBytes[0] == (byte)'M' && rawBytes[1] == (byte)'Z')
            return true;
        if (rawBytes.Length >= 4 && rawBytes[0] == 0x7F && rawBytes[1] == (byte)'E' && rawBytes[2] == (byte)'L' && rawBytes[3] == (byte)'F')
            return true;
        if (ext is ".txt" or ".md" or ".json" or ".csv" or ".log" && rawBytes.AsSpan(0, Math.Min(rawBytes.Length, 4096)).IndexOf((byte)0) >= 0)
            return true;
        return false;
    }
}
