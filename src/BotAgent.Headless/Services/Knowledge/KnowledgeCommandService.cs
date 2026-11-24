using System.Text;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

namespace BotAgent.Services.Knowledge;

/// <summary>
/// 确定性 <c>//kb</c> 会话知识库命令处理器：
/// 不经过 LLM、不写入聊天上下文历史，严格受插件开关与会话档案特征开关约束。
/// </summary>
public sealed class KnowledgeCommandService
{
    public const string HelpText = """
        [会话轻量知识库 //kb]
        - //kb save <标题> | <正文>：保存笔记到当前会话知识库
        - //kb save-doc [latest|文件名] [标题]：提取当前会话暂存文档入库
        - //kb search <关键词>：BM25 检索当前会话知识库
        - //kb list：列出当前会话已入库文档与 docId
        - //kb forget <docId>：立即从当前会话知识库删除并移出索引
        """;

    private readonly IQqChatSource _source;
    private readonly SettingsBox _box;
    private readonly IPlatformPolicyResolver _platformPolicies;
    private readonly PluginManager _plugins;
    private readonly PluginGate _pluginGate;
    private readonly KnowledgeHostPortsAdapter _ports;
    private readonly ConversationRegistry? _registry;
    private readonly IAccessRoleResolver? _roles;

    public KnowledgeCommandService(
        IQqChatSource source,
        SettingsBox box,
        IPlatformPolicyResolver platformPolicies,
        PluginManager plugins,
        PluginGate pluginGate,
        KnowledgeHostPortsAdapter ports,
        ConversationRegistry? registry = null,
        IAccessRoleResolver? roles = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _platformPolicies = platformPolicies ?? throw new ArgumentNullException(nameof(platformPolicies));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _pluginGate = pluginGate ?? throw new ArgumentNullException(nameof(pluginGate));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _registry = registry;
        _roles = roles;
    }

    public static bool IsCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = StripLeadingAtMentions(text);
        return trimmed.Equals("//kb", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//kb ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//kb\n", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//kb\r", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//kb\t", StringComparison.OrdinalIgnoreCase);
    }

    public bool TryHandle(QqChatMessage msg)
    {
        if (!msg.AllowsCommands || !IsCommand(msg.Text)) return false;
        _ = HandleAsync(msg);
        return true;
    }

    public async Task<bool> HandleAsync(QqChatMessage msg)
    {
        if (!msg.AllowsCommands || !IsCommand(msg.Text)) return false;
        var lease = PlatformSourceExecution.Current;
        var sourceKey = _source is IPlatformSourceRouter router
            && router.ConversationKey(msg, k => _registry?.Find(k) is not null) is { Length: > 0 } routedKey
            ? routedKey
            : Channels.Key(msg.Channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);
        _ports.ObserveSourceContext(msg, sourceKey);

        var reply = await ExecuteCommandAsync(msg, sourceKey, lease).ConfigureAwait(false);
        await SendReplyAsync(msg, lease, reply).ConfigureAwait(false);
        return true;
    }

    private async Task<string> ExecuteCommandAsync(QqChatMessage msg, string sourceKey, PlatformSourceLease? lease)
    {
        var settings = _box.Current;
        if (!settings.AiModeEnabled)
            return "机器人当前处于静默关闭状态（AI 总开关已关闭）。";

        if (!_pluginGate.Allows(PluginGate.KnowledgeCapability)
            || !_plugins.TryGetContribution<IKnowledgeContribution>("preset.feature.knowledge", out var contribution)
            || contribution is null
            || !contribution.IsReady)
        {
            return "当前会话未启用轻量知识库能力（preset.feature.knowledge）。";
        }

        var context = lease is not null
            ? lease.Context
            : new PlatformContext(PlatformId.Normalize(msg.Channel), AccountScope.Legacy, string.Empty);
        var platformPolicy = _platformPolicies.Resolve(context);
        var effective = ConversationProfileResolver.Resolve(settings, platformPolicy, sourceKey);
        if (!effective.FeatureEnabled("knowledge"))
            return "当前会话档案或平台策略已关闭轻量知识库。";

        var raw = StripLeadingAtMentions(msg.Text);
        var body = raw.Length > 4 ? raw[4..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(body) || body.Equals("help", StringComparison.OrdinalIgnoreCase))
            return HelpText;

        var splitIdx = body.IndexOfAny([' ', '\t', '\r', '\n']);
        var verb = (splitIdx < 0 ? body : body[..splitIdx]).ToLowerInvariant();
        var rest = splitIdx < 0 ? string.Empty : body[(splitIdx + 1)..].Trim();

        if (_roles is not null)
        {
            var subject = InboundSubjectIdentity.Resolve(msg, context);
            if (_roles.TryResolveExplicitRole(subject, sourceKey, out var explicitRole) && explicitRole == WorkspaceRole.Guest)
                return "当前账号在会话中被标记为 Guest（仅聊天），已禁止访问 //kb 知识库命令。";
        }

        if (msg.IsGroup && verb is "save" or "save-doc" or "forget"
            && !IsAuthorizedGroupKnowledgeOperator(msg, sourceKey, context, settings.ApprovalApprovers))
        {
            return "群聊知识库写入与删除仅限群主、群管理员或已配置审批人操作。";
        }

        return verb switch
        {
            "save" => HandleSaveText(contribution, sourceKey, rest),
            "save-doc" => await HandleSaveDocAsync(contribution, sourceKey, rest).ConfigureAwait(false),
            "search" => HandleSearch(contribution, sourceKey, rest),
            "list" => HandleList(contribution, sourceKey),
            "forget" => HandleForget(contribution, sourceKey, rest),
            _ => HelpText
        };
    }

    private bool IsAuthorizedGroupKnowledgeOperator(QqChatMessage msg, string sourceKey, PlatformContext context, string? approversRaw)
    {
        var explicitDemoted = false;
        if (_roles is not null)
        {
            var subject = InboundSubjectIdentity.Resolve(msg, context);
            if (_roles.TryResolveExplicitRole(subject, sourceKey, out var explicitRole))
            {
                if (explicitRole >= WorkspaceRole.GroupManager) return true;
                if (explicitRole == WorkspaceRole.Guest) return false;
                explicitDemoted = true;
            }
        }

        if (!explicitDemoted && !Channels.IsOfficial(msg.Channel) && !Channels.IsAliasId(msg.UserId)
            && msg.SenderRole is "owner" or "admin")
            return true;

        var approvers = ApprovalStore.NormalizeApprovers(approversRaw);
        if (approvers.Contains("user:" + msg.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            return true;
        if (msg.Envelope is { IsReliableIdentity: true } env
            && !string.IsNullOrWhiteSpace(env.Message.Sender.NativeUserId)
            && approvers.Contains("user:" + env.Message.Sender.NativeUserId))
            return true;
        return false;
    }

    private static string StripLeadingAtMentions(string? rawText)
    {
        var text = (rawText ?? string.Empty).TrimStart();
        while (text.StartsWith('@'))
        {
            var space = text.IndexOf(' ');
            if (space <= 1 || space > 20) break;
            var handle = text[1..space];
            if (!handle.All(char.IsDigit) && handle != "全体成员") break;
            text = text[(space + 1)..].TrimStart();
        }
        return text;
    }

    private static string HandleSaveText(IKnowledgeContribution contribution, string sourceKey, string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return "用法：//kb save <标题> | <正文>";

        string title;
        string content;
        var nlIdx = rest.IndexOfAny(['\r', '\n']);
        var firstLine = nlIdx >= 0 ? rest[..nlIdx] : rest;
        var pipeIdx = firstLine.IndexOf('|');
        if (pipeIdx > 0)
        {
            title = rest[..pipeIdx].Trim();
            content = rest[(pipeIdx + 1)..].Trim();
        }
        else
        {
            if (nlIdx <= 0)
                return "用法：//kb save <标题> | <正文>";
            title = rest[..nlIdx].Trim();
            content = rest[(nlIdx + 1)..].Trim();
        }

        var result = contribution.SaveText(sourceKey, title, content, "note");
        return result.Ok && result.Document is { } doc
            ? $"已入库：{doc.DocId}《{MessageMarkers.EscapeExternalControlTags(doc.Title)}》（{doc.ChunkCount} 段，{doc.TotalChars} 字）"
            : $"入库失败：{result.ReasonCode}";
    }

    private async Task<string> HandleSaveDocAsync(IKnowledgeContribution contribution, string sourceKey, string rest)
    {
        if (!_pluginGate.Allows(PluginGate.DocumentsCapability))
            return "当前未启用文档读取能力（preset.feature.documents）。";

        string? selector = "latest";
        string? titleOverride = null;
        if (!string.IsNullOrWhiteSpace(rest))
        {
            var parts = rest.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            selector = parts[0];
            if (parts.Length > 1) titleOverride = parts[1];
        }

        var result = await contribution.SaveAttachmentAsync(sourceKey, selector, titleOverride, CancellationToken.None).ConfigureAwait(false);
        return result.Ok && result.Document is { } doc
            ? $"附件已入库：{doc.DocId}《{MessageMarkers.EscapeExternalControlTags(doc.Title)}》（{doc.ChunkCount} 段，{doc.TotalChars} 字）"
            : $"附件入库失败：{result.ReasonCode}";
    }

    private static string HandleSearch(IKnowledgeContribution contribution, string sourceKey, string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return "用法：//kb search <关键词>";
        var hits = contribution.Search(sourceKey, rest, 0);
        if (hits.Length == 0)
            return "未检索到匹配的本会话知识条目。";

        var sb = new StringBuilder();
        sb.AppendLine($"[本会话知识库命中 {hits.Length} 段]");
        foreach (var hit in hits)
        {
            var safeTitle = MessageMarkers.EscapeExternalControlTags(hit.Title);
            var snippet = MessageMarkers.EscapeExternalControlTags(hit.Snippet.Replace('\r', ' ').Replace('\n', ' '));
            if (snippet.Length > 160)
            {
                var safeLen = char.IsHighSurrogate(snippet[159]) ? 159 : 160;
                snippet = snippet[..safeLen] + "…";
            }
            sb.AppendLine($"• [{hit.DocId}] {safeTitle} #{hit.ChunkIndex + 1} (BM25={hit.Score:F2})：{snippet}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string HandleList(IKnowledgeContribution contribution, string sourceKey)
    {
        var docs = contribution.List(sourceKey);
        if (docs.Length == 0)
            return "当前会话知识库暂无文档。";

        var sb = new StringBuilder();
        sb.AppendLine($"[当前会话知识库共 {docs.Length} 篇]");
        foreach (var doc in docs.Take(20))
            sb.AppendLine($"• {doc.DocId}｜{MessageMarkers.EscapeExternalControlTags(doc.Title)}（{doc.SourceKind}，{doc.ChunkCount} 段，{doc.TotalChars} 字）");
        if (docs.Length > 20)
            sb.AppendLine($"…其余 {docs.Length - 20} 篇已省略");
        return sb.ToString().TrimEnd();
    }

    private static string HandleForget(IKnowledgeContribution contribution, string sourceKey, string rest)
    {
        var docId = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(docId))
            return "用法：//kb forget <docId>";
        return contribution.Forget(sourceKey, docId)
            ? $"已遗忘并移出当前会话知识库索引：{docId}"
            : $"当前会话未找到知识条目：{docId}";
    }

    private async Task SendReplyAsync(QqChatMessage msg, PlatformSourceLease? lease, string reply)
    {
        try
        {
            if (lease is not null && !lease.IsCurrent) return;
            using var scope = PlatformSourceExecution.Enter(lease);
            if (msg.Envelope is { } env && _source is IPlatformSourceRouter router)
            {
                var target = router.CompatibilityTarget(ConversationIdCodec.Encode(env.Message.Ref.Conversation));
                await _source.SendTextAsync(target.IsGroup, target.Id, reply).ConfigureAwait(false);
                return;
            }
            await _source.SendTextAsync(msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId, reply).ConfigureAwait(false);
        }
        catch
        {
            // 命令回复失败静默吞吐，避免异常外泄或打断入站循环。
        }
    }
}
