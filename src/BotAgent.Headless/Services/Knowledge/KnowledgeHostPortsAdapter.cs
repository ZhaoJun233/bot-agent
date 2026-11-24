using System.Collections.Concurrent;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Identity;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services.Documents;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

namespace BotAgent.Services.Knowledge;

/// <summary>
/// <c>preset.feature.knowledge</c> 的宿主端口适配器：
/// 1) 始终由宿主从实时会话上下文与来源租约派生复合隔离作用域（绝不采信模型/参数传来的作用域）；
/// 2) 严格同会话暂存附件读取（禁止外网 URL 抓取），仅调用 <c>preset.feature.documents</c> 提取正文；
/// 3) 监听 <see cref="IUnifiedMemory.Invalidated"/>，在会话触发遗忘/抑止时联动清空同作用域知识库。
/// </summary>
public sealed class KnowledgeHostPortsAdapter : IKnowledgeHostPorts, IDisposable
{
    private const int MaxTrackedConversations = 1024;

    private readonly IKnowledgeStore _store;
    private readonly Func<IReadOnlyDictionary<string, JsonElement>?> _readSettings;
    private readonly Action<string> _log;
    private readonly DocumentHostPortsAdapter? _documents;
    private readonly PluginManager? _plugins;
    private readonly IUnifiedMemory? _unifiedMemory;
    private readonly SettingsBox? _box;
    private readonly IPlatformPolicyResolver? _platformPolicies;
    private readonly IIdentityLinkResolver? _identityLinks;

    private readonly ConcurrentDictionary<string, (DriverKey Driver, ConversationId? Conversation, string CompositeScope)> _scopes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (bool IsReliable, IdentityEndpointKey Endpoint)> _attestedEndpoints = new(StringComparer.Ordinal);

    public KnowledgeHostPortsAdapter(
        IKnowledgeStore store,
        Func<IReadOnlyDictionary<string, JsonElement>?> readSettings,
        Action<string> log,
        DocumentHostPortsAdapter? documents = null,
        PluginManager? plugins = null,
        IUnifiedMemory? unifiedMemory = null,
        SettingsBox? box = null,
        IPlatformPolicyResolver? platformPolicies = null,
        IIdentityLinkResolver? identityLinks = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readSettings = readSettings ?? throw new ArgumentNullException(nameof(readSettings));
        _log = log ?? (_ => { });
        _documents = documents;
        _plugins = plugins;
        _unifiedMemory = unifiedMemory;
        _box = box;
        _platformPolicies = platformPolicies;
        _identityLinks = identityLinks;
        if (_unifiedMemory is not null)
        {
            _unifiedMemory.Invalidated += OnUnifiedMemoryInvalidated;
            PurgeSuppressedScopes();
        }
    }

    private static string EscapeScopeSegment(string? value)
        => (value ?? string.Empty).Replace("%", "%25", StringComparison.Ordinal).Replace("|", "%7C", StringComparison.Ordinal);

    public static string BuildCompositeScope(DriverKey driver, string sourceKey)
        => $"{EscapeScopeSegment(driver.PlatformId)}|{EscapeScopeSegment(driver.AccountScope)}|{EscapeScopeSegment(driver.InstanceId)}|{EscapeScopeSegment(sourceKey)}";

    public KnowledgeOptions ReadOptions()
    {
        var values = _readSettings();
        if (values is null) return new KnowledgeOptions();
        var maxDocs = Math.Clamp(ReadInt(values, "maxDocsPerScope", 256), 16, 256);
        var maxChunkChars = Math.Clamp(ReadInt(values, "maxChunkChars", 800), 200, 1600);
        var defaultTopK = Math.Clamp(ReadInt(values, "defaultTopK", 4), 1, 10);
        return new KnowledgeOptions(maxDocs, KnowledgeOptionsDefaults.MaxChunksPerScope, maxChunkChars, defaultTopK);
    }

    public KnowledgeSaveResult SaveText(string sourceKey, string title, string content, string sourceKind)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
            return new(false, "source_required");
        var scope = ResolveScope(sourceKey);
        if (IsSuppressed(scope))
            return new(false, "conversation_suppressed");
        var options = ReadOptions();
        var result = _store.SaveDocument(scope.CompositeScope, title, content, sourceKind, options);
        if (result.Ok)
            _log($"[Knowledge] saved docId={result.Document?.DocId} chunks={result.Document?.ChunkCount} chars={result.Document?.TotalChars}");
        return result;
    }

    public async Task<KnowledgeSaveResult> SaveAttachmentAsync(
        string sourceKey, string? attachmentSelector, string? titleOverride, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sourceKey))
            return new(false, "source_required");
        var scope = ResolveScope(sourceKey);
        if (IsSuppressed(scope))
            return new(false, "conversation_suppressed");
        if (_documents is null || _plugins is null)
            return new(false, "documents_unavailable");

        var selector = string.IsNullOrWhiteSpace(attachmentSelector) ? "latest" : attachmentSelector.Trim();
        if (selector.Contains("://", StringComparison.Ordinal))
            return new(false, "url_not_allowed_for_knowledge_attachment");

        if (!_plugins.TryGetContribution<IDocumentContribution>("preset.feature.documents", out var docPlugin)
            || docPlugin is null || !docPlugin.IsReady)
            return new(false, "documents_plugin_unavailable");

        var fetched = await _documents.FetchAsync(sourceKey, selector, ct).ConfigureAwait(false);
        if (!fetched.Ok || fetched.Data.Length == 0)
            return new(false, fetched.ReasonCode);

        var extracted = docPlugin.ExtractBytes(fetched.FileName, fetched.Data, section: null, maxChars: 12000);
        if (!extracted.Ok || string.IsNullOrWhiteSpace(extracted.Text))
            return new(false, string.IsNullOrWhiteSpace(extracted.ReasonCode) ? "extract_failed" : extracted.ReasonCode);

        var title = string.IsNullOrWhiteSpace(titleOverride) ? fetched.FileName : titleOverride.Trim();
        return SaveText(sourceKey, title, extracted.Text, "attachment");
    }

    public KnowledgeSearchHit[] Search(string sourceKey, string query, int topK)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(query))
            return [];
        var scope = ResolveScope(sourceKey);
        if (IsSuppressed(scope))
        {
            if (!scope.CompositeScope.StartsWith("shared:", StringComparison.Ordinal))
                _store.ClearScope(scope.CompositeScope);
            return [];
        }
        var options = ReadOptions();
        var k = topK > 0 ? Math.Clamp(topK, 1, 10) : options.DefaultTopK;
        return _store.Search(scope.CompositeScope, query, k);
    }

    public KnowledgeDocumentItem[] List(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return [];
        var scope = ResolveScope(sourceKey);
        if (IsSuppressed(scope))
        {
            if (!scope.CompositeScope.StartsWith("shared:", StringComparison.Ordinal))
                _store.ClearScope(scope.CompositeScope);
            return [];
        }
        return _store.ListDocuments(scope.CompositeScope);
    }

    public bool Forget(string sourceKey, string docId)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(docId))
            return false;
        var scope = ResolveScope(sourceKey);
        return _store.ForgetDocument(scope.CompositeScope, docId);
    }

    public void ClearConversation(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return;
        _attestedEndpoints.TryRemove(sourceKey, out _);
        if (_scopes.TryRemove(sourceKey, out var tracked))
        {
            if (!tracked.CompositeScope.StartsWith("shared:", StringComparison.Ordinal))
                _store.ClearScope(tracked.CompositeScope);
            return;
        }
        var resolved = ResolveLocalScopeOnly(sourceKey);
        _store.ClearScope(resolved.CompositeScope);
    }

    public void ObserveSourceContext(QqChatMessage msg, string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return;
        _ = ResolveScope(sourceKey, msg);
    }

    private (DriverKey Driver, ConversationId? Conversation, string CompositeScope) ResolveScope(string sourceKey, QqChatMessage? msg = null)
    {
        if (string.Equals(sourceKey, "workspace:owner", StringComparison.Ordinal))
        {
            var sharedWs = _identityLinks?.ResolveWorkspaceSharedScope(IdentityResourceKind.Knowledge);
            var wsScope = !string.IsNullOrWhiteSpace(sharedWs) ? $"shared:{sharedWs}" : "workspace:owner";
            return (new DriverKey("workspace", "owner", string.Empty), null, wsScope);
        }

        if (msg?.Envelope is { IsReliableIdentity: true } env && UnifiedMemoryTurn.AttestedContext(env) is { } attested)
        {
            string? shared = null;
            if (attested.Conversation.Kind == ConversationKind.PrivateChat && attested.Actor is not null)
            {
                var endpoint = new IdentityEndpointKey(attested.Driver.PlatformId, attested.Driver.AccountScope, attested.Driver.InstanceId ?? string.Empty, attested.Actor.NativeUserId);
                RecordAttestedEndpoint(sourceKey, endpoint);
                shared = _identityLinks?.ResolveSharedOwnerScope(endpoint, IdentityResourceKind.Knowledge);
            }
            var scope = !string.IsNullOrWhiteSpace(shared)
                ? $"shared:{shared}"
                : BuildCompositeScope(attested.Driver, sourceKey);
            var bound = (attested.Driver, (ConversationId?)attested.Conversation, scope);
            if (string.IsNullOrWhiteSpace(shared)) CacheScope(sourceKey, bound);
            return bound;
        }

        if (PlatformSourceExecution.Current is { IsCurrent: true } current)
        {
            var dk = new DriverKey(current.Context.PlatformId, current.Context.AccountScope, current.Context.InstanceId);
            var conv = current.Origin.Trigger?.Conversation
                ?? (ConversationIdCodec.TryParse(sourceKey, out var parsedFromKey) ? parsedFromKey : null);
            var sharedLive = _attestedEndpoints.TryGetValue(sourceKey, out var att) && att.IsReliable
                ? _identityLinks?.ResolveSharedOwnerScope(att.Endpoint, IdentityResourceKind.Knowledge)
                : null;
            var scope = !string.IsNullOrWhiteSpace(sharedLive)
                ? $"shared:{sharedLive}"
                : BuildCompositeScope(dk, sourceKey);
            var bound = (dk, conv, scope);
            if (string.IsNullOrWhiteSpace(sharedLive)) CacheScope(sourceKey, bound);
            return bound;
        }

        if (_scopes.TryGetValue(sourceKey, out var cached))
        {
            if (_attestedEndpoints.TryGetValue(sourceKey, out var att) && att.IsReliable
                && _identityLinks?.ResolveSharedOwnerScope(att.Endpoint, IdentityResourceKind.Knowledge) is { Length: > 0 } sharedCached)
            {
                return (cached.Driver, cached.Conversation, $"shared:{sharedCached}");
            }
            return cached;
        }

        if (ConversationIdCodec.TryParse(sourceKey, out var parsedConv))
        {
            var parsedDriver = new DriverKey(
                parsedConv.PlatformId,
                parsedConv.AccountScope,
                ResolveDeclaredInstanceId(parsedConv.PlatformId, parsedConv.AccountScope));
            // 未经认证端点登记的纯字符串查键，不跨越单端物理知识库作用域。
            var scope = BuildCompositeScope(parsedDriver, sourceKey);
            var parsedResolved = (parsedDriver, (ConversationId?)parsedConv, scope);
            CacheScope(sourceKey, parsedResolved);
            return parsedResolved;
        }

        var channel = Channels.ChannelOf(sourceKey);
        var platformId = channel switch
        {
            Channels.Official => PlatformId.QqOfficial,
            Channels.Feishu => PlatformId.Feishu,
            Channels.Local => PlatformId.Local,
            _ => PlatformId.QqPrivate
        };
        var accountScope = platformId == PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy;
        var fallbackDriver = new DriverKey(platformId, accountScope, ResolveDeclaredInstanceId(platformId, accountScope));
        var legacyScope = BuildCompositeScope(fallbackDriver, sourceKey);
        var resolved = (fallbackDriver, (ConversationId?)null, legacyScope);
        CacheScope(sourceKey, resolved);
        return resolved;
    }

    private (DriverKey Driver, ConversationId? Conversation, string CompositeScope) ResolveLocalScopeOnly(string sourceKey)
    {
        if (string.Equals(sourceKey, "workspace:owner", StringComparison.Ordinal))
            return (new DriverKey("workspace", "owner", string.Empty), null, "workspace:owner");
        if (ConversationIdCodec.TryParse(sourceKey, out var parsedConv))
        {
            var d = new DriverKey(parsedConv.PlatformId, parsedConv.AccountScope, ResolveDeclaredInstanceId(parsedConv.PlatformId, parsedConv.AccountScope));
            return (d, parsedConv, BuildCompositeScope(d, sourceKey));
        }
        var ch = Channels.ChannelOf(sourceKey);
        var pid = ch switch
        {
            Channels.Official => PlatformId.QqOfficial,
            Channels.Feishu => PlatformId.Feishu,
            Channels.Local => PlatformId.Local,
            _ => PlatformId.QqPrivate
        };
        var acc = pid == PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy;
        var fd = new DriverKey(pid, acc, ResolveDeclaredInstanceId(pid, acc));
        return (fd, null, BuildCompositeScope(fd, sourceKey));
    }

    private void RecordAttestedEndpoint(string sourceKey, IdentityEndpointKey endpoint)
    {
        if (_attestedEndpoints.Count >= MaxTrackedConversations && !_attestedEndpoints.ContainsKey(sourceKey))
        {
            foreach (var stale in _attestedEndpoints.Keys.Take(64).ToArray())
                _attestedEndpoints.TryRemove(stale, out _);
        }
        _attestedEndpoints[sourceKey] = (true, endpoint);
    }

    private string ResolveDeclaredInstanceId(string platformId, string accountScope)
    {
        var declared = _box?.Current.DeclaredPlatformDrivers ?? default;
        if (declared.IsDefaultOrEmpty) return string.Empty;
        DriverKey? match = null;
        var count = 0;
        var norm = PlatformId.Normalize(platformId);
        foreach (var d in declared)
        {
            if (string.Equals(PlatformId.Normalize(d.PlatformId), norm, StringComparison.Ordinal)
                && string.Equals(d.AccountScope, accountScope, StringComparison.Ordinal))
            {
                match = d;
                if (++count > 1) return string.Empty;
            }
        }
        return count == 1 && match is { } found ? found.InstanceId : string.Empty;
    }

    private void CacheScope(string sourceKey, (DriverKey Driver, ConversationId? Conversation, string CompositeScope) entry)
    {
        if (_scopes.Count >= MaxTrackedConversations && !_scopes.ContainsKey(sourceKey))
        {
            foreach (var stale in _scopes.Keys.Take(64).ToArray())
                _scopes.TryRemove(stale, out _);
        }
        if (entry.Conversation is null && _scopes.TryGetValue(sourceKey, out var prev)
            && prev.Driver == entry.Driver && prev.Conversation is not null)
        {
            entry = (entry.Driver, prev.Conversation, entry.CompositeScope);
        }
        _scopes[sourceKey] = entry;
    }

    private bool IsSuppressed((DriverKey Driver, ConversationId? Conversation, string CompositeScope) scope)
        => scope.Conversation is { } conv && _unifiedMemory?.IsConversationSuppressed(scope.Driver, conv) == true;

    private void OnUnifiedMemoryInvalidated(UnifiedMemoryInvalidation notification)
    {
        if (!string.Equals(notification.Reason, "forgotten", StringComparison.OrdinalIgnoreCase) || _unifiedMemory is null)
            return;
        PurgeSuppressedScopes();
    }

    private void PurgeSuppressedScopes()
    {
        if (_unifiedMemory is null) return;
        foreach (var pair in _scopes.ToArray())
        {
            if (IsSuppressed(pair.Value))
                _store.ClearScope(pair.Value.CompositeScope);
        }
        foreach (var suppressed in _unifiedMemory.GetSuppressedScopes())
        {
            _store.ClearScope(BuildCompositeScope(suppressed.Driver, ConversationIdCodec.Encode(suppressed.Conversation)));
            if (suppressed.Conversation.ThreadId is null
                && long.TryParse(suppressed.Conversation.NativeTargetId, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var numericTarget)
                && suppressed.Conversation.Kind is ConversationKind.GroupChat or ConversationKind.PrivateChat)
            {
                var channel = suppressed.Driver.PlatformId switch
                {
                    PlatformId.QqOfficial => Channels.Official,
                    PlatformId.Feishu => Channels.Feishu,
                    PlatformId.Local => Channels.Local,
                    _ => Channels.Private
                };
                var legacyKey = Channels.Key(channel, suppressed.Conversation.Kind == ConversationKind.GroupChat, numericTarget);
                _store.ClearScope(BuildCompositeScope(suppressed.Driver, legacyKey));
            }
        }
    }

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var elem)) return fallback;
        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var i)) return i;
        if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out var parsed)) return parsed;
        return fallback;
    }

    public void Dispose()
    {
        if (_unifiedMemory is not null)
            _unifiedMemory.Invalidated -= OnUnifiedMemoryInvalidated;
    }

    private static class KnowledgeOptionsDefaults
    {
        public const int MaxChunksPerScope = 4096;
    }
}
