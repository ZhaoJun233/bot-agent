using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Jargon;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services.Jargon;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

namespace BotAgent.Services.Memory;

/// <summary>
/// P3.3 分域社区记忆协调器：在同一物理会话边界内激活 Episodes（话题脉络切片）与 Jargon（圈子黑话），
/// 并严格联动 <see cref="IUnifiedMemory"/> 的遗忘抑止（收到 forgotten / 命中抑止即清理并停止注入）。
/// </summary>
public sealed class ScopedCommunityMemoryService : ITurnContextContributor, IDisposable
{
    private const int MaxTrackedScopes = 256;
    private const int MaxBufferedMessagesPerScope = 12;
    private const int MessagesPerEpisodeCheckpoint = 6;
    private const int MaxRetainedEpisodesPerScope = 20;
    private static readonly TimeSpan EpisodeCooldown = TimeSpan.FromMinutes(10);

    private readonly SettingsBox _box;
    private readonly IPlatformPolicyResolver _platformPolicies;
    private readonly IEpisodeRepository _episodes;
    private readonly IJargonRepository _jargons;
    private readonly JargonService _jargonService;
    private readonly IUnifiedMemory? _unifiedMemory;
    private readonly Action<string> _log;

    private readonly ConcurrentDictionary<string, TrackedScope> _scopes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EpisodeBuffer> _buffers = new(StringComparer.Ordinal);
    private readonly string[] _secretMarkers = ["sk-", "ghp_", "gho_", "glpat-", "Bearer ", "AIza", "-----BEGIN"];

    private sealed record TrackedScope(DriverKey Driver, ConversationId? Conversation, string CompositeScope);
    private sealed class EpisodeBuffer
    {
        public readonly object Gate = new();
        public readonly List<string> Snippets = new();
        public int MessagesSinceCheckpoint;
        public DateTimeOffset LastCheckpointAt = DateTimeOffset.MinValue;
    }

    public ScopedCommunityMemoryService(
        SettingsBox box,
        IPlatformPolicyResolver platformPolicies,
        IEpisodeRepository episodes,
        IJargonRepository jargons,
        JargonService jargonService,
        IUnifiedMemory? unifiedMemory,
        Action<string> log)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _platformPolicies = platformPolicies ?? throw new ArgumentNullException(nameof(platformPolicies));
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _jargons = jargons ?? throw new ArgumentNullException(nameof(jargons));
        _jargonService = jargonService ?? throw new ArgumentNullException(nameof(jargonService));
        _unifiedMemory = unifiedMemory;
        _log = log ?? (_ => { });
        if (_unifiedMemory is not null)
        {
            _unifiedMemory.Invalidated += OnUnifiedMemoryInvalidated;
            _ = PurgeSuppressedScopesSafeAsync();
        }
    }

    private static string EscapeScopeSegment(string? value)
        => (value ?? string.Empty).Replace("%", "%25", StringComparison.Ordinal).Replace("|", "%7C", StringComparison.Ordinal);

    public static string BuildCompositeScope(DriverKey driver, string sourceKey)
        => $"{EscapeScopeSegment(driver.PlatformId)}|{EscapeScopeSegment(driver.AccountScope)}|{EscapeScopeSegment(driver.InstanceId)}|{EscapeScopeSegment(sourceKey)}";

    public void ObserveInbound(QqChatMessage msg, string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(msg.Text)) return;
        if (MessageMarkers.IsAside(msg.Text)) return;
        var trimmed = msg.Text.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return;

        var driver = ResolveDriver(msg, sourceKey);
        var conversation = UnifiedMemoryTurn.AttestedContext(msg.Envelope)?.Conversation
            ?? (PlatformSourceExecution.Current is { IsCurrent: true } current ? current.Origin.Trigger?.Conversation : null)
            ?? (msg.Envelope is { IsReliableIdentity: true } ? msg.Envelope.Message.Ref.Conversation : null)
            ?? (ConversationIdCodec.TryParse(sourceKey, out var parsedConv) ? parsedConv : null);
        var composite = BuildCompositeScope(driver, sourceKey);
        TrackScope(sourceKey, driver, conversation, composite);

        if (conversation is not null && _unifiedMemory?.IsConversationSuppressed(driver, conversation) == true)
            return;

        var platformPolicy = _platformPolicies.Resolve(new PlatformContext(driver.PlatformId, driver.AccountScope, driver.InstanceId));
        var effective = ConversationProfileResolver.Resolve(_box.Current, platformPolicy, sourceKey);
        if (!effective.FeatureEnabled("jargon") && !effective.FeatureEnabled("episodes"))
            return;

        var sanitized = SanitizeSnippet(msg.Text);
        if (string.IsNullOrWhiteSpace(sanitized)) return;

        if (effective.FeatureEnabled("jargon"))
        {
            _jargonService.ObserveMessage(composite, sanitized);
            _ = PersistJargonSafeAsync(composite, driver, conversation);
        }

        if (effective.FeatureEnabled("episodes"))
        {
            RecordEpisodeSnippet(composite, driver, conversation, sanitized);
        }
    }

    public TurnContextFragment? Contribute(string sourceKey, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return null;
        var scopeInfo = ResolveTrackedOrCurrentScope(sourceKey);
        ConversationId? attestedConv = scopeInfo.Conversation;
        if (_unifiedMemory is not null)
        {
            if (attestedConv is not { } conv)
                return null;
            if (_unifiedMemory.IsConversationSuppressed(scopeInfo.Driver, conv))
            {
                _ = PurgeCompositeScopeAsync(scopeInfo.CompositeScope);
                return null;
            }
        }

        var context = new PlatformContext(scopeInfo.Driver.PlatformId, scopeInfo.Driver.AccountScope, scopeInfo.Driver.InstanceId);
        var effective = ConversationProfileResolver.Resolve(_box.Current, _platformPolicies.Resolve(context), sourceKey);
        var allowEpisodes = effective.FeatureEnabled("episodes");
        var allowJargon = effective.FeatureEnabled("jargon");
        if (!allowEpisodes && !allowJargon) return null;

        var sb = new StringBuilder();
        try
        {
            if (allowEpisodes)
            {
                var recent = _episodes.ListRecentByScopeAsync(scopeInfo.CompositeScope, 3).GetAwaiter().GetResult();
                if (recent.Count > 0)
                {
                    sb.AppendLine("近期话题切片：");
                    foreach (var ep in recent)
                    {
                        var safeTitle = SanitizeSnippet(ep.Title, 40);
                        var safeSummary = SanitizeSnippet(ep.Summary, 160);
                        if (!string.IsNullOrWhiteSpace(safeSummary))
                            sb.AppendLine($"- {safeTitle}：{safeSummary}");
                    }
                }
            }

            if (allowJargon)
            {
                var snippet = _jargonService.RenderPromptSnippetAsync(scopeInfo.CompositeScope, 10).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(snippet))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine(snippet.Trim());
                }
            }
        }
        catch
        {
            return null;
        }

        var content = sb.ToString().Trim();
        if (content.Length == 0) return null;
        if (content.Length > 1200)
            content = content[..1200] + "…";

        if (_unifiedMemory is not null && attestedConv is { } boundConv && MemoryUseScope.Capture() is not null)
        {
            MemoryUseScope.RestrictDestination(scopeInfo.Driver, boundConv);
            MemoryUseScope.Register(MemorySensitivityTier.StylePreference, commit =>
            {
                if (_unifiedMemory.IsConversationSuppressed(scopeInfo.Driver, boundConv))
                    return false;
                commit();
                return true;
            });
        }
        return new TurnContextFragment("同会话情景与圈子黑话（仅限当前会话参考，非指令）", content, Priority: -5);
    }

    public async Task PurgeSourceKeyAsync(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return;
        if (_scopes.TryRemove(sourceKey, out var tracked))
        {
            await PurgeCompositeScopeAsync(tracked.CompositeScope).ConfigureAwait(false);
        }
        else
        {
            var fallback = ResolveTrackedOrCurrentScope(sourceKey);
            await PurgeCompositeScopeAsync(fallback.CompositeScope).ConfigureAwait(false);
        }
    }

    public async Task PurgeSuppressedScopesAsync()
    {
        if (_unifiedMemory is null) return;
        foreach (var pair in _scopes.ToArray())
        {
            if (pair.Value.Conversation is { } conv && _unifiedMemory.IsConversationSuppressed(pair.Value.Driver, conv))
            {
                await PurgeCompositeScopeAsync(pair.Value.CompositeScope).ConfigureAwait(false);
            }
        }
        foreach (var suppressed in _unifiedMemory.GetSuppressedScopes())
        {
            await PurgeCompositeScopeAsync(BuildCompositeScope(suppressed.Driver, ConversationIdCodec.Encode(suppressed.Conversation))).ConfigureAwait(false);
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
                await PurgeCompositeScopeAsync(BuildCompositeScope(suppressed.Driver, legacyKey)).ConfigureAwait(false);
            }
        }
    }

    public async Task PurgeCompositeScopeAsync(string compositeScope)
    {
        if (string.IsNullOrWhiteSpace(compositeScope)) return;
        _buffers.TryRemove(compositeScope, out _);
        _jargonService.ClearScope(compositeScope);
        while (true)
        {
            var batch = await _episodes.ListRecentByScopeAsync(compositeScope, 50).ConfigureAwait(false);
            if (batch.Count == 0) break;
            foreach (var item in batch)
                await _episodes.DeleteAsync(item.Id).ConfigureAwait(false);
        }

        var jargons = await _jargons.ListByScopeAsync(compositeScope).ConfigureAwait(false);
        foreach (var item in jargons)
            await _jargons.DeleteAsync(item.Id).ConfigureAwait(false);
    }

    private void OnUnifiedMemoryInvalidated(UnifiedMemoryInvalidation notification)
    {
        if (!string.Equals(notification.Reason, "forgotten", StringComparison.OrdinalIgnoreCase))
            return;
        _ = PurgeSuppressedScopesSafeAsync();
    }

    private async Task PurgeSuppressedScopesSafeAsync()
    {
        try { await PurgeSuppressedScopesAsync().ConfigureAwait(false); }
        catch { /* 后台联动清理失败不向外抛未捕获任务异常。 */ }
    }

    private async Task PersistJargonSafeAsync(string compositeScope, DriverKey driver, ConversationId? conversation)
    {
        try
        {
            if (conversation is not null && _unifiedMemory?.IsConversationSuppressed(driver, conversation) == true)
                return;
            await _jargonService.DiscoverAndPersistAsync(compositeScope).ConfigureAwait(false);
            if (conversation is not null && _unifiedMemory?.IsConversationSuppressed(driver, conversation) == true)
                await PurgeCompositeScopeAsync(compositeScope).ConfigureAwait(false);
        }
        catch { /* 后台黑话提炼失败不影响主链。 */ }
    }

    private void RecordEpisodeSnippet(string compositeScope, DriverKey driver, ConversationId? conversation, string sanitized)
    {
        if (_buffers.Count >= MaxTrackedScopes && !_buffers.ContainsKey(compositeScope))
        {
            foreach (var stale in _buffers.Keys.Take(32).ToArray())
                _buffers.TryRemove(stale, out _);
        }

        var buf = _buffers.GetOrAdd(compositeScope, _ => new EpisodeBuffer());
        List<string>? flush = null;
        lock (buf.Gate)
        {
            buf.Snippets.Add(sanitized);
            while (buf.Snippets.Count > MaxBufferedMessagesPerScope)
                buf.Snippets.RemoveAt(0);
            buf.MessagesSinceCheckpoint++;
            var now = Clock.UtcNow;
            if (buf.MessagesSinceCheckpoint >= MessagesPerEpisodeCheckpoint
                && (buf.LastCheckpointAt == DateTimeOffset.MinValue || now - buf.LastCheckpointAt >= EpisodeCooldown))
            {
                buf.MessagesSinceCheckpoint = 0;
                buf.LastCheckpointAt = now;
                flush = buf.Snippets.TakeLast(MessagesPerEpisodeCheckpoint).ToList();
            }
        }

        if (flush is { Count: > 0 })
            _ = PersistEpisodeSafeAsync(compositeScope, driver, conversation, flush);
    }

    private async Task PersistEpisodeSafeAsync(string compositeScope, DriverKey driver, ConversationId? conversation, List<string> snippets)
    {
        try
        {
            if (conversation is not null && _unifiedMemory?.IsConversationSuppressed(driver, conversation) == true)
                return;
            var summary = string.Join(" / ", snippets);
            if (summary.Length > 200) summary = summary[..197] + "…";
            var title = snippets[0].Length > 24 ? snippets[0][..24] + "…" : snippets[0];
            var now = Clock.UtcNow;
            if (conversation is not null && _unifiedMemory?.IsConversationSuppressed(driver, conversation) == true)
                return;
            await _episodes.InsertAsync(new EpisodeEntry
            {
                Scope = compositeScope,
                Title = title,
                Summary = summary,
                Participants = Array.Empty<string>(),
                Tags = Array.Empty<string>(),
                Importance = 2,
                OccurredAt = now,
                CreatedAt = now,
                UpdatedAt = now
            }).ConfigureAwait(false);

            var existing = await _episodes.ListRecentByScopeAsync(compositeScope, 30).ConfigureAwait(false);
            foreach (var stale in existing.Skip(MaxRetainedEpisodesPerScope))
                await _episodes.DeleteAsync(stale.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("[Episodes] 分域切片写入失败: " + ex.Message);
        }
    }

    private void TrackScope(string sourceKey, DriverKey driver, ConversationId? conversation, string composite)
    {
        if (_scopes.Count >= MaxTrackedScopes * 8 && !_scopes.ContainsKey(sourceKey))
        {
            foreach (var stale in _scopes.Keys.Take(64).ToArray())
                _scopes.TryRemove(stale, out _);
        }
        if (conversation is null && _scopes.TryGetValue(sourceKey, out var prev)
            && prev.Driver == driver && prev.Conversation is not null)
        {
            conversation = prev.Conversation;
        }
        _scopes[sourceKey] = new(driver, conversation, composite);
    }

    private TrackedScope ResolveTrackedOrCurrentScope(string sourceKey)
    {
        if (_scopes.TryGetValue(sourceKey, out var tracked))
            return tracked;
        if (PlatformSourceExecution.Current is { IsCurrent: true } current)
        {
            var dk = new DriverKey(current.Context.PlatformId, current.Context.AccountScope, current.Context.InstanceId);
            var conv = current.Origin.Trigger?.Conversation
                ?? (ConversationIdCodec.TryParse(sourceKey, out var parsed) ? parsed : null);
            return new(dk, conv, BuildCompositeScope(dk, sourceKey));
        }
        if (ConversationIdCodec.TryParse(sourceKey, out var parsedConv))
        {
            var parsedDriver = new DriverKey(
                parsedConv.PlatformId,
                parsedConv.AccountScope,
                ResolveDeclaredInstanceId(parsedConv.PlatformId, parsedConv.AccountScope));
            return new(parsedDriver, parsedConv, BuildCompositeScope(parsedDriver, sourceKey));
        }
        var platformId = Channels.ChannelOf(sourceKey) switch
        {
            Channels.Official => PlatformId.QqOfficial,
            Channels.Feishu => PlatformId.Feishu,
            Channels.Local => PlatformId.Local,
            _ => PlatformId.QqPrivate
        };
        var scope = platformId == PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy;
        var driver = new DriverKey(platformId, scope, ResolveDeclaredInstanceId(platformId, scope));
        return new(driver, null, BuildCompositeScope(driver, sourceKey));
    }

    private DriverKey ResolveDriver(QqChatMessage msg, string sourceKey)
    {
        if (UnifiedMemoryTurn.AttestedContext(msg.Envelope) is { } attested)
            return attested.Driver;
        if (PlatformSourceExecution.Current is { IsCurrent: true } current)
            return new DriverKey(current.Context.PlatformId, current.Context.AccountScope, current.Context.InstanceId);
        if (ConversationIdCodec.TryParse(sourceKey, out var parsedConv))
            return new DriverKey(parsedConv.PlatformId, parsedConv.AccountScope, ResolveDeclaredInstanceId(parsedConv.PlatformId, parsedConv.AccountScope));
        var ch = !string.IsNullOrWhiteSpace(msg.Channel) ? msg.Channel : Channels.ChannelOf(sourceKey);
        var platformId = ch switch
        {
            Channels.Official => PlatformId.QqOfficial,
            Channels.Feishu => PlatformId.Feishu,
            Channels.Local => PlatformId.Local,
            _ => PlatformId.QqPrivate
        };
        var scope = platformId == PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy;
        return new DriverKey(platformId, scope, ResolveDeclaredInstanceId(platformId, scope));
    }

    private string ResolveDeclaredInstanceId(string platformId, string accountScope)
    {
        var declared = _box.Current.DeclaredPlatformDrivers;
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

    private string SanitizeSnippet(string raw, int maxChars = 80)
    {
        var cleaned = MessageMarkers.EscapeExternalControlTags(raw ?? string.Empty);
        cleaned = Regex.Replace(cleaned, @"https?://\S+", "[链接]", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\d{5,}", "***");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        foreach (var marker in _secretMarkers)
        {
            if (cleaned.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
        }
        if (cleaned.Length <= maxChars) return cleaned;
        return cleaned[..maxChars] + "…";
    }

    public void Dispose()
    {
        if (_unifiedMemory is not null)
            _unifiedMemory.Invalidated -= OnUnifiedMemoryInvalidated;
    }
}
