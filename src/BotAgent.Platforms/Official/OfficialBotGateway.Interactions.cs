using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Official;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Qq;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Official;

public sealed partial class OfficialBotGateway
{
    private const int IntentsGroupMember = 1 << 24;
    private const int IntentsInteraction = 1 << 26;
    private const int MaxSeenInteractions = 512;
    private const int MaxCachedOfficialGroups = 256;
    private static readonly TimeSpan InteractionDedupTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OfficialGroupCacheTtl = TimeSpan.FromMinutes(30);

    private static readonly AsyncLocal<string?> AmbientDispatchEventId = new();
    private Func<IReadOnlyDictionary<string, JsonElement>?>? _readPluginSettings;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenInteractions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string EventId, DateTimeOffset SeenAt)> _interactionReplyEvents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string? Name, DateTimeOffset ExpiresAt)> _officialGroupNames = new(StringComparer.Ordinal);
    private volatile bool _extraIntentsDowngraded;
    private volatile bool _markdownPermissionDenied;
    private volatile bool _keyboardPermissionDenied;
    private volatile bool _groupNamePermissionDenied;
    private int _lastSettingsMask = -1;

    public OfficialBotGateway BindPluginSettings(Func<IReadOnlyDictionary<string, JsonElement>?>? reader)
    {
        _readPluginSettings = reader;
        SyncPluginSettingsGeneration();
        return this;
    }

    public bool EnableInteractiveMarkdown
    {
        get
        {
            SyncPluginSettingsGeneration();
            return ReadOfficialBool("enableInteractiveMarkdown", false);
        }
    }

    public bool SubscribeMemberEvents
    {
        get
        {
            SyncPluginSettingsGeneration();
            return ReadOfficialBool("subscribeMemberEvents", false);
        }
    }

    public bool IsExtraIntentsDowngraded => _extraIntentsDowngraded;
    public bool IsMarkdownPermissionDenied => _markdownPermissionDenied;
    public bool IsKeyboardPermissionDenied => _keyboardPermissionDenied;
    public int CurrentIdentifyIntents => ComputeIdentifyIntents();

    public void NotifyPluginSettingsChanged()
    {
        _extraIntentsDowngraded = false;
        _markdownPermissionDenied = false;
        _keyboardPermissionDenied = false;
        _groupNamePermissionDenied = false;
        _officialGroupNames.Clear();
        ClearInteractionReplyEvents();
        SessionId = string.Empty;
        _lastSettingsMask = CurrentSettingsMask();
    }

    private int CurrentSettingsMask()
        => (ReadOfficialBool("enableInteractiveMarkdown", false) ? 1 : 0)
         | (ReadOfficialBool("subscribeMemberEvents", false) ? 2 : 0);

    private void SyncPluginSettingsGeneration()
    {
        var mask = CurrentSettingsMask();
        var prev = Interlocked.Exchange(ref _lastSettingsMask, mask);
        if (prev >= 0 && prev != mask)
        {
            _extraIntentsDowngraded = false;
            _markdownPermissionDenied = false;
            _keyboardPermissionDenied = false;
            _groupNamePermissionDenied = false;
            ClearInteractionReplyEvents();
            SessionId = string.Empty;
        }
    }

    private bool ReadOfficialBool(string key, bool fallback)
    {
        var values = _readPluginSettings?.Invoke();
        if (values is null || !values.TryGetValue(key, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b : fallback,
            _ => fallback
        };
    }

    private int ComputeIdentifyIntents()
    {
        SyncPluginSettingsGeneration();
        var intents = IntentsGroupAndC2C;
        if (_extraIntentsDowngraded)
            return intents;
        if (ReadOfficialBool("enableInteractiveMarkdown", false))
            intents |= IntentsInteraction;
        if (ReadOfficialBool("subscribeMemberEvents", false))
            intents |= IntentsGroupMember;
        return intents;
    }

    public void ObserveWebSocketCloseCode(int? closeStatus)
    {
        if (closeStatus is not (4009 or 4013 or 4014))
            return;
        if (!_extraIntentsDowngraded && ComputeIdentifyIntents() != IntentsGroupAndC2C)
        {
            _extraIntentsDowngraded = true;
            SessionId = string.Empty;
            Log($"official_intents downgraded=base close_code={closeStatus.Value}");
        }
    }

    private void HandleInteractionCreate(JsonObject d)
    {
        if (!EnableInteractiveMarkdown)
        {
            Log("official_interaction reason=disabled");
            return;
        }

        var interactionId = ReadOptionalStrictId(d["id"]);
        if (interactionId is null)
        {
            Log("official_interaction reason=identity_missing");
            return;
        }

        var chatType = AsInt(d["chat_type"]);
        if (chatType == 0)
        {
            Log("official_interaction reason=guild_unsupported");
            return;
        }

        var groupOpenId = ReadOptionalStrictId(d["group_openid"]);
        var isGroup = chatType switch
        {
            1 => true,
            2 => false,
            null => groupOpenId is not null,
            _ => (bool?)null
        };
        if (isGroup is null)
        {
            Log("official_interaction reason=chat_type_unsupported");
            return;
        }

        var operatorOpenId = isGroup.Value
            ? ReadOptionalStrictId(d["group_member_openid"])
            : ReadOptionalStrictId(d["user_openid"]);

        if (operatorOpenId is null || (isGroup.Value && groupOpenId is null))
        {
            Log("official_interaction reason=operator_missing");
            return;
        }

        var now = Clock.UtcNow;
        PruneInteractionsIfNeeded(now);
        if (_seenInteractions.TryGetValue(interactionId, out var seenAt) && now - seenAt <= InteractionDedupTtl)
        {
            Log("official_interaction reason=duplicate");
            return;
        }
        _seenInteractions[interactionId] = now;

        _ = AckInteractionSafeAsync(interactionId);

        var targetOpenId = isGroup.Value ? groupOpenId! : operatorOpenId;
        var eventTime = RoleEventTime(d["timestamp"]) ?? Clock.Now;
        var message = MessageIdentity(isGroup.Value, targetOpenId, interactionId, OfficialMessageDirection.Inbound, null);
        if (!AcceptInboundDelivery(message, eventTime))
            return;
        RememberInteractionReplyEvent(interactionId, AmbientDispatchEventId.Value, now);

        var messageAlias = _ids.Value.AliasFor(message);
        var buttonData = Text(d["data"]?["resolved"]?["button_data"])?.Trim();
        var safeBody = string.IsNullOrWhiteSpace(buttonData) ? "[按钮交互]" : BoundedMediaText(buttonData!, 500);
        // Only exact approval verbs ("同意/拒绝 <6-char id>") or private-only //memory card callbacks may carry AllowsCommands=true; all other callbacks stay untrusted.
        var allowsApprovalVerb = !string.IsNullOrWhiteSpace(buttonData)
            && ApprovalFlow.TryParseCommand(safeBody) is not null;
        var allowsPrivateMemoryCallback = !isGroup.Value
            && BotAgent.Platforms.Common.MemoryPrivateCardHelper.IsSafePrivateMemoryButtonCommand(safeBody);

        if (isGroup.Value)
        {
            var groupAlias = _ids.Value.AliasFor(TargetIdentity(true, groupOpenId!));
            var subject = new OfficialIdentityKey(new(_applicationId, OfficialIdentityKind.GroupMember, message.Scope.Conversation), operatorOpenId);
            var userAlias = _ids.Value.AliasFor(subject);
            var role = PeekMemberRole(subject);
            MessageReceived?.Invoke(new QqChatMessage(
                MessageId: messageAlias,
                IsGroup: true,
                UserId: userAlias,
                GroupId: groupAlias,
                SenderName: DisplayName(d, userAlias),
                Text: safeBody,
                Time: eventTime,
                MentionedSelf: true,
                Channel: Channels.Official)
            {
                IsReliableIdentity = true,
                OfficialRole = role,
                AllowsCommands = allowsApprovalVerb
            });
            return;
        }

        var c2cAlias = _ids.Value.AliasFor(TargetIdentity(false, operatorOpenId));
        MessageReceived?.Invoke(new QqChatMessage(
            MessageId: messageAlias,
            IsGroup: false,
            UserId: c2cAlias,
            GroupId: 0,
            SenderName: DisplayName(d, c2cAlias),
            Text: safeBody,
            Time: eventTime,
            MentionedSelf: true,
            Channel: Channels.Official)
        {
            IsReliableIdentity = true,
            AllowsCommands = allowsApprovalVerb || allowsPrivateMemoryCallback
        });
    }

    private async Task AckInteractionSafeAsync(string interactionId)
    {
        using var op = _operations.TryEnter(CancellationToken.None);
        if (_disposed || op is null) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(op.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var token = await EnsureTokenAsync(false, cts.Token).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/interactions/{Uri.EscapeDataString(interactionId)}")
            {
                Content = JsonBody(new JsonObject { ["code"] = 0 })
            };
            Auth(req, token);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            Log($"official_interaction_ack status={(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log("official_interaction_ack reason=transport_failed");
        }
        catch (OperationCanceledException) { }
    }

    private async Task<string?> TryQueryOfficialGroupNameAsync(long groupId, CancellationToken ct)
    {
        if (!SubscribeMemberEvents || _groupNamePermissionDenied || _disposed || groupId <= 0)
            return null;

        using var op = _operations.TryEnter(ct);
        if (op is null || !ApplicationIsCurrent() || !TryLoadIdentities())
            return null;

        var groupOpenId = ResolveTarget(isGroup: true, groupId);
        if (string.IsNullOrWhiteSpace(groupOpenId))
            return null;

        var now = Clock.UtcNow;
        if (_officialGroupNames.TryGetValue(groupOpenId, out var cached) && now < cached.ExpiresAt)
            return cached.Name;

        try
        {
            var token = await EnsureTokenAsync(false, op.Token).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/v2/groups/{Uri.EscapeDataString(groupOpenId)}/info");
            Auth(req, token);
            using var resp = await _http.SendAsync(req, op.Token).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            var bytes = await OfficialApiResult.ReadBoundedAsync(resp.Content, op.Token).ConfigureAwait(false);
            if (status == 403)
            {
                _groupNamePermissionDenied = true;
                return null;
            }
            if (bytes is { Length: > 0 })
            {
                var node = JsonNode.Parse(Encoding.UTF8.GetString(bytes)) as JsonObject;
                var code = AsInt(node?["code"]);
                if (code == 11253)
                {
                    _groupNamePermissionDenied = true;
                    return null;
                }
                var name = (Text(node?["group_name"]) ?? Text(node?["data"]?["group_name"]) ?? Text(node?["name"]))?.Trim();
                if (resp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(name))
                {
                    PruneOfficialGroupsIfNeeded(now);
                    _officialGroupNames[groupOpenId] = (name, now.Add(OfficialGroupCacheTtl));
                    return name;
                }
            }

            PruneOfficialGroupsIfNeeded(now);
            _officialGroupNames[groupOpenId] = (null, now.AddMinutes(10));
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void PruneOfficialGroupsIfNeeded(DateTimeOffset now)
    {
        if (_officialGroupNames.Count < MaxCachedOfficialGroups) return;
        foreach (var pair in _officialGroupNames)
        {
            if (now >= pair.Value.ExpiresAt)
                _officialGroupNames.TryRemove(pair.Key, out _);
        }
        if (_officialGroupNames.Count < MaxCachedOfficialGroups) return;
        foreach (var oldest in _officialGroupNames.OrderBy(p => p.Value.ExpiresAt).Take(MaxCachedOfficialGroups / 8 + 1).Select(p => p.Key).ToArray())
            _officialGroupNames.TryRemove(oldest, out _);
    }

    private static bool ShouldSendOfficialMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("```", StringComparison.Ordinal)
            || text.Contains("\n| ", StringComparison.Ordinal)
            || text.StartsWith("# ", StringComparison.Ordinal)
            || text.StartsWith("## ", StringComparison.Ordinal)
            || text.StartsWith("### ", StringComparison.Ordinal);
    }

    private static string? ReadOptionalStrictId(JsonNode? node)
    {
        if (node is not JsonValue val || !val.TryGetValue<string>(out var raw) || string.IsNullOrEmpty(raw))
            return null;
        return raw.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? null : raw;
    }

    private void RememberInteractionReplyEvent(string interactionId, string? dispatchEventId, DateTimeOffset now)
    {
        var evt = dispatchEventId?.Trim();
        if (string.IsNullOrEmpty(evt)) return;
        if (_interactionReplyEvents.Count >= MaxSeenInteractions)
        {
            foreach (var pair in _interactionReplyEvents)
            {
                if (now - pair.Value.SeenAt > InteractionDedupTtl)
                    _interactionReplyEvents.TryRemove(pair.Key, out _);
            }
        }
        if (_interactionReplyEvents.ContainsKey(interactionId) || _interactionReplyEvents.Count < MaxSeenInteractions)
            _interactionReplyEvents[interactionId] = (evt, now);
    }

    private bool TryGetInteractionReplyEvent(string interactionId, out string eventId)
    {
        if (_interactionReplyEvents.TryGetValue(interactionId, out var entry) && !string.IsNullOrEmpty(entry.EventId))
        {
            eventId = entry.EventId;
            return true;
        }
        eventId = string.Empty;
        return false;
    }

    private void ClearInteractionReplyEvents()
        => _interactionReplyEvents.Clear();

    private void PruneInteractionsIfNeeded(DateTimeOffset now)
    {
        if (_seenInteractions.Count >= MaxSeenInteractions)
        {
            foreach (var pair in _seenInteractions)
            {
                if (now - pair.Value > InteractionDedupTtl)
                    _seenInteractions.TryRemove(pair.Key, out _);
            }
        }
        if (_interactionReplyEvents.Count >= MaxSeenInteractions)
        {
            foreach (var pair in _interactionReplyEvents)
            {
                if (now - pair.Value.SeenAt > InteractionDedupTtl)
                    _interactionReplyEvents.TryRemove(pair.Key, out _);
            }
        }
    }
}
