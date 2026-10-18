using System.Collections.Concurrent;
using BotAgent.Services.OneBot;
using BotAgent.Domain.Qq;
using BotAgent.Platforms;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Services.Platforms;
using BotAgent.Domain.Conversation;

namespace BotAgent.Services.Qq;

/// <summary>
/// 多通道聚合：把「私域（NapCat）」与「官方（QQ 开放平台）」两个上行合成**一个**
/// <see cref="IQqChatSource"/> 交给上层，上层照旧只认 (isGroup, targetId)。
///
/// 为什么要这层，而不是让 BotAgentHost 到处判断通道：
///   机器人的发送点有十几处（回复、分句、语音、表情包、点歌、戳一戳、面板代发…），
///   每处都加一个 channel 参数 = 十几处都要改对，改错一处就是“串台”。
///   这里改成**按目标号路由**：官方通道的目标号是它自己申请的**别名号**（<see cref="Channels.AliasBase"/> 起步，
///   见官方网关的 OfficialIdMap），与真实 QQ 号天然不重叠 —— 于是“这个号属于哪个通道”
///   从一个约定变成可判定的规则，发送点一行都不用改。
///
/// 事件方向则相反：入站消息会在 <see cref="QqChatMessage.Channel"/> 上打标，
/// 上层用 <see cref="Channels.Key"/> 拼会话 key —— 会话隔离就是这么落地的。
/// </summary>
public sealed class ChannelRouter : IQqChatSource, IChannelRegistry, IPlatformSourceOwner, IPlatformSourceRouter, IDisposable
{
    private readonly List<IQqChatSource> _sources;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, IQqChatSource> _byChannel = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>学到的「目标 → 通道」（入站时记，重启后靠别名区间兜底）。</summary>
    private readonly ConcurrentDictionary<(bool IsGroup, long Id), string> _learned = new();

    /// <summary>会话 key → 通道：面板/命令侧拿到的 key 也能反查（含别名区间之外的异常情况）。</summary>
    private readonly ConcurrentDictionary<string, string> _byKey = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();
    private bool _disposed;
    private readonly IPlatformRegistry? _platformRegistry;
    private readonly PlatformDriverOperations? _drivers;
    private readonly Func<IPlatformAdapter, bool>? _identityIsCurrent;
    private Func<PlatformContext, bool, long, string, bool>? _outboundAllowed;
    private readonly List<Action> _detachTrusted = new();

    public ChannelRouter(IEnumerable<IQqChatSource> sources, Action<string>? log = null)
        : this(sources, log, null, null) { }

    public ChannelRouter(IEnumerable<IQqChatSource> sources, IPlatformRegistry registry,
        PlatformDriverOperations drivers, Action<string>? log = null, Func<IPlatformAdapter, bool>? identityIsCurrent = null)
        : this(sources, log, registry, drivers, identityIsCurrent) { }

    private ChannelRouter(IEnumerable<IQqChatSource> sources, Action<string>? log,
        IPlatformRegistry? registry, PlatformDriverOperations? drivers, Func<IPlatformAdapter, bool>? identityIsCurrent = null)
    {
        _sources = [.. sources.Where(s => s is not null)];
        _log = log;
        _platformRegistry = registry;
        _drivers = drivers;
        _identityIsCurrent = identityIsCurrent;

        foreach (var src in _sources)
        {
            if (_sources.Count(candidate => string.Equals(candidate.Channel, src.Channel, StringComparison.OrdinalIgnoreCase)) == 1)
                _byChannel[src.Channel] = src;
            if (_platformRegistry is not null)
            {
                SubscribeTrusted(src);
                continue;
            }
            src.MessageReceived += OnMessage;
            src.Poked += OnPoked;
            src.MessageRecalled += OnRecall;
            src.ConnectionChanged += OnConnection;
        }

        if (_sources.Count == 0)
        {
            throw new ArgumentException("ChannelRouter 至少要有一个上行通道", nameof(sources));
        }
    }

    /// <summary>路由器本身不是一个「通道」—— 判渠道请用 <see cref="Channels.ChannelOf(string?)"/> 或消息上的 Channel。</summary>
    public string Channel => Channels.Private;

    public event Action<QqChatMessage>? MessageReceived;

    public event Action<QqPokeEvent>? Poked;

    public event Action<QqRecallEvent>? MessageRecalled;

    public event Action<bool>? ConnectionChanged;

    /// <summary>至少一路在线（面板/健康检查用）。</summary>
    public bool IsConnected => _sources.Any(s => s.IsConnected);

    // ---------- 通道台账（面板分块、命令过滤都靠它）----------

    public IReadOnlyList<IQqChatSource> Sources => _sources;

    public IQqChatSource? Get(string channel)
        => _byChannel.TryGetValue(channel, out var src) ? src : null;

    public bool IsEnabled(string channel) => _byChannel.ContainsKey(channel);

    /// <summary>对外的一句话状态：私域 在线 / 官方 未启用 ……</summary>
    public string DescribeChannels()
    {
        var parts = new List<string>();
        foreach (var channel in new[] { Channels.Private, Channels.Official })
        {
            if (_byChannel.TryGetValue(channel, out var src))
            {
                parts.Add($"{Channels.Display(channel)} {(src.IsConnected ? "在线" : "离线")}");
            }
            else if (Channels.IsOfficial(channel))
            {
                parts.Add($"{Channels.Display(channel)} 未启用");
            }
        }

        return string.Join("，", parts);
    }

    // ---------- 入站：打上通道标签再往上走 ----------

    private (IPlatformAdapter Adapter, PlatformDriverRegistration Driver)? Binding(IQqChatSource source)
    {
        var matches = _platformRegistry!.Adapters.Where(adapter => ReferenceEquals(adapter, source)
            || adapter is IPlatformSourceBinding binding && ReferenceEquals(binding.Source, source)).Take(2).ToArray();
        if (matches.Length != 1) return null;
        var adapter = matches[0];
        var context = adapter.Context;
        var driver = _drivers!.TryGet(new(PlatformId.Normalize(context.PlatformId), context.AccountScope, context.InstanceId));
        return driver is not null && ReferenceEquals(driver.Adapter, adapter) ? (adapter, driver) : null;
    }

    private PlatformSourceLease? Capture(IQqChatSource source, MessageRef? trigger = null)
    {
        var binding = Binding(source);
        if (binding is not { } bound) return null;
        var context = bound.Adapter.Context;
        var lifetime = bound.Driver.Lifetime;
        bool Current() => !_disposed && (_identityIsCurrent?.Invoke(bound.Adapter) ?? true) && bound.Driver.IsActive && !lifetime.IsCancellationRequested
            && ReferenceEquals(_platformRegistry!.GetAdapter(context), bound.Adapter);
        if (!Current()) return null;
        return new(this, source, new(bound.Driver.Key, trigger), lifetime, Current);
    }

    PlatformSourceLease? IPlatformSourceRouter.CaptureForConversation(string sourceKey) => CaptureForConversation(sourceKey);

    public PlatformSourceLease? CaptureForConversation(string sourceKey)
    {
        if (_platformRegistry is null || !ConversationIdCodec.TryParse(sourceKey, out var conversation)) return null;
        var matches = _sources.Where(source => MatchesConversation(source, conversation)).Take(2).ToArray();
        return matches.Length == 1 ? Capture(matches[0]) : null;
    }

    private bool MatchesConversation(IQqChatSource source, ConversationId conversation)
    {
        if (Binding(source) is not { } binding) return false;
        var context = binding.Adapter.Context;
        if (PlatformId.Normalize(context.PlatformId) != PlatformId.Normalize(conversation.PlatformId)) return false;
        if (source is IFeishuTargetMap native && conversation.AccountScope == AccountScope.Legacy
            && long.TryParse(conversation.NativeTargetId, out var alias))
            return native.TryGetTargetConversation(conversation.Kind == ConversationKind.GroupChat, alias, out _);
        return string.Equals(context.AccountScope, conversation.AccountScope, StringComparison.Ordinal);
    }

    string? IPlatformSourceOwner.ConversationKey(QqChatMessage message, Func<string, bool> exists)
        => ConversationKey(message, exists);

    public string? ConversationKey(QqChatMessage message, Func<string, bool> exists)
    {
        var lease = PlatformSourceExecution.Current;
        if (lease is null || !ReferenceEquals(lease.Owner, this) || !lease.IsCurrent) return null;
        var conversation = message.Envelope?.Message.Ref.Conversation ?? new ConversationId(
            lease.Context.PlatformId, lease.Context.AccountScope,
            message.IsGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat,
            (message.IsGroup ? message.GroupId : message.UserId).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!MatchesConversation(lease.Source, conversation)) return null;
        var key = ConversationIdCodec.Encode(conversation);
        if (!ConversationIdCodec.TryParse(key, out _)) return null;
        if (lease.Source is IFeishuTargetMap native && native.TryGetTargetAlias(conversation, out var alias))
        {
            var oldKey = Channels.Key(Channels.Feishu, message.IsGroup, alias);
            if (exists(oldKey)) return exists(key) && key != oldKey ? null : oldKey;
        }
        return key;
    }

    public (bool IsGroup, long Id) CompatibilityTarget(string sourceKey)
    {
        if (!ConversationIdCodec.TryParse(sourceKey, out var conversation)) return (false, 0);
        var lease = PlatformSourceExecution.Current;
        if (lease is null || !ReferenceEquals(lease.Owner, this) || !MatchesConversation(lease.Source, conversation))
            lease = CaptureForConversation(sourceKey);
        if (lease is null || !lease.IsCurrent) return (false, 0);
        var isGroup = conversation.Kind == ConversationKind.GroupChat;
        if (lease.Source is IFeishuTargetMap native)
        {
            if (conversation.AccountScope == AccountScope.Legacy && long.TryParse(conversation.NativeTargetId, out var oldAlias)
                && native.TryGetTargetConversation(isGroup, oldAlias, out _)) return (isGroup, oldAlias);
            return native.TryGetTargetAlias(conversation, out var alias) ? (isGroup, alias) : (false, 0);
        }
        return long.TryParse(conversation.NativeTargetId, out var id) && id > 0 ? (isGroup, id) : (false, 0);
    }

    private void SubscribeTrusted(IQqChatSource source)
    {
        var bound = Binding(source) ?? throw new ArgumentException("source_adapter_binding_required");
        if (bound.Adapter is not IPlatformInboundChatSource rich)
            throw new ArgumentException("source_rich_binding_required");
        void Message(QqChatMessage legacy, InboundMessage native)
        {
            PlatformSourceLease? lease;
            PlatformInboundEnvelope envelope;
            try
            {
                lease = Capture(source, native.Ref);
                if (lease is null) return;
                envelope = new(lease.Origin, native);
            }
            catch (ArgumentException) { _log?.Invoke("invalid_platform_envelope"); return; }
            var channel = Channels.Declared(source.Channel);
            if (string.IsNullOrEmpty(channel) || !lease.IsCurrent) return;
            Learn(channel, legacy.IsGroup, legacy.IsGroup ? legacy.GroupId : legacy.UserId);
            using var scope = PlatformSourceExecution.Enter(lease);
            MessageReceived?.Invoke(legacy with { Channel = channel, Envelope = envelope, SourceLifetime = lease.Lifetime });
        }
        void Poke(QqPokeEvent value)
        {
            var lease = Capture(source);
            if (lease is null) return;
            using var scope = PlatformSourceExecution.Enter(lease);
            Poked?.Invoke(value with { Channel = source.Channel, Origin = lease.Origin, SourceLifetime = lease.Lifetime });
        }
        void Recall(QqRecallEvent value)
        {
            var lease = Capture(source);
            if (lease is null) return;
            using var scope = PlatformSourceExecution.Enter(lease);
            MessageRecalled?.Invoke(value with { Channel = source.Channel, Origin = lease.Origin, SourceLifetime = lease.Lifetime });
        }
        rich.RichInboundReceived += Message;
        source.Poked += Poke;
        source.MessageRecalled += Recall;
        source.ConnectionChanged += OnConnection;
        _detachTrusted.Add(() => { rich.RichInboundReceived -= Message; source.Poked -= Poke;
            source.MessageRecalled -= Recall; source.ConnectionChanged -= OnConnection; });
    }

    private void OnMessage(QqChatMessage msg)
    {
        // 归一上行标签；显式未知通道必须在学习路由或转发前拒绝。
        // 空标签仍按旧 OneBot 私域通道处理。
        var channel = Channels.Declared(msg.Channel);
        if (string.IsNullOrEmpty(channel) || !_byChannel.ContainsKey(channel))
        {
            _log?.Invoke("入站路由拒绝：未知或未注册通道");
            return;
        }
        Learn(channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);
        MessageReceived?.Invoke(string.Equals(msg.Channel, channel, StringComparison.OrdinalIgnoreCase)
            ? msg
            : msg with { Channel = channel });
    }

    private void OnPoked(QqPokeEvent evt)
    {
        var channel = ResolveChannel(evt.IsGroup, evt.GroupId);
        Poked?.Invoke(evt with { Channel = channel });
    }

    private void OnRecall(QqRecallEvent evt)
    {
        var channel = ResolveChannel(evt.IsGroup, evt.GroupId);
        MessageRecalled?.Invoke(evt with { Channel = channel });
    }

    private void OnConnection(bool up)
    {
        // 单通道的在线/离线由各自网关自己打日志（那边知道是谁断了）；这里只上报聚合状态，
        // 免得“官方掉线”被上层当成“QQ 掉线”去走重连/告警那套。
        // 注：日志前缀由调用方（FileLog.Write("Channel", …)）给，这里不要再写一遍 [Channel]。
        _log?.Invoke($"上行状态变化：{DescribeChannels()}");
        ConnectionChanged?.Invoke(IsConnected);
    }

    private void Learn(string channel, bool isGroup, long id)
    {
        if (id > 0)
        {
            _learned[(isGroup, id)] = channel;
            _byKey[Channels.Key(channel, isGroup, id)] = channel;
        }
    }

    /// <summary>把会话 key 登记进来（上层从库里恢复会话时调）—— 面板代发这类“没有入站消息”的场景靠它定通道。</summary>
    public void RegisterKey(string sourceKey)
    {
        var channel = Channels.ChannelOf(sourceKey);
        var (isGroup, id) = Channels.Parse(sourceKey);
        if (id > 0 && !string.IsNullOrEmpty(channel) && _byChannel.ContainsKey(channel))
        {
            _byKey[sourceKey] = channel;
            _learned[(isGroup, id)] = channel;
        }
    }

    /// <summary>通道 → 归属判定：先看学到的，再看别名区间（别名是官方通道自己发的号段）。</summary>
    private string ResolveChannel(bool isGroup, long id)
    {
        if (_learned.TryGetValue((isGroup, id), out var learned))
        {
            return learned;
        }

        return Channels.IsLocalId(id)
            ? Channels.Local
            : Channels.IsFeishuId(id)
                ? Channels.Feishu
                : Channels.IsAliasId(id)
                    ? Channels.Official
                    : Channels.Private;
    }

    internal void BindOutboundAuthorization(Func<PlatformContext, bool, long, string, bool> authorize)
    {
        if (_outboundAllowed is not null) throw new InvalidOperationException("platform_outbound_already_bound");
        _outboundAllowed = authorize ?? throw new ArgumentNullException(nameof(authorize));
    }

    private IQqChatSource? Resolve(bool isGroup, long id, string feature = "text")
    {
        if (_disposed) return null;
        if (PlatformSourceExecution.Current is { } lease)
            return ReferenceEquals(lease.Owner, this) && lease.IsCurrent
                && (_outboundAllowed?.Invoke(lease.Context, isGroup, id, feature) ?? true) ? lease.Source : null;
        var channel = ResolveChannel(isGroup, id);
        if (_byChannel.TryGetValue(channel, out var src))
        {
            if (_platformRegistry is not null)
            {
                var autonomous = Capture(src);
                if (autonomous is null || !(_outboundAllowed?.Invoke(autonomous.Context, isGroup, id, feature) ?? true)) return null;
            }
            return src;
        }

        _log?.Invoke($"出站路由拒绝：通道未注册或未启用（{channel}，目标形状={(isGroup ? "group" : "private")}）");
        return null;
    }

    private static Task<bool> UnsupportedBoolAsync() => Task.FromResult(false);
    private static Task<SendResult> UnsupportedSendAsync() => Task.FromResult(new SendResult(false));
    private static Task<(string? Text, long SenderId)> UnsupportedMessageAsync() =>
        Task.FromResult<(string? Text, long SenderId)>((null, 0));

    // ---------- 出站：按目标号路由 ----------

    public Task<bool> SendMusicAsync(bool isGroup, long targetId, string platform, string songId, string title = "", CancellationToken ct = default)
        => Resolve(isGroup, targetId, "music")?.SendMusicAsync(isGroup, targetId, platform, songId, title, ct) ?? UnsupportedBoolAsync();

    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
        => Resolve(isGroup, targetId)?.SendTextAsync(isGroup, targetId, text, ct, replyToMessageId, directAddress) ?? UnsupportedSendAsync();

    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => ResolveRead("quote", messageId)?.GetMessageInfoAsync(messageId, ct) ?? UnsupportedMessageAsync();

    public Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
        => Resolve(isGroup, targetId, "voice")?.SendVoiceAsync(isGroup, targetId, audioUrl, ct) ?? UnsupportedBoolAsync();

    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => Resolve(isGroup, targetId, "image")?.SendImageAsync(isGroup, targetId, data, ct, replyToMessageId) ?? UnsupportedBoolAsync();

    public Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default)
        => Resolve(isGroup, targetId, "poke")?.SendPokeAsync(isGroup, targetId, userId, ct) ?? UnsupportedBoolAsync();

    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
        => Resolve(true, groupId)?.GetGroupNameAsync(groupId, ct) ?? Task.FromResult<string?>(null);

    public Task<GroupMemberInfo?> GetGroupMemberInfoAsync(long groupId, long userId, CancellationToken ct = default)
        => Resolve(true, groupId)?.GetGroupMemberInfoAsync(groupId, userId, ct) ?? Task.FromResult<GroupMemberInfo?>(null);

    public Task<List<string>> FetchCustomFacesAsync(int count = 48, CancellationToken ct = default)
    {
        if (_platformRegistry is null)
            return _byChannel.TryGetValue(Channels.Private, out var legacy) ? legacy.FetchCustomFacesAsync(count, ct)
                : Task.FromResult(new List<string>());
        var lease = PlatformSourceExecution.Current;
        if (lease is null)
        {
            var candidates = _sources.Where(source => Binding(source)?.Adapter.Context.PlatformId == PlatformId.QqPrivate).Take(2).ToArray();
            if (candidates.Length == 1) lease = Capture(candidates[0]);
        }
        return lease is not null && ReferenceEquals(lease.Owner, this) && lease.IsCurrent
            && lease.Context.PlatformId == PlatformId.QqPrivate
            && (_outboundAllowed?.Invoke(lease.Context, false, 0, "customfaces") ?? true)
            ? lease.Source.FetchCustomFacesAsync(count, ct) : Task.FromResult(new List<string>());
    }

    private IQqChatSource? ResolveRead(string feature, long messageId)
    {
        if (_platformRegistry is null) return Resolve(false, messageId, feature);
        if (PlatformSourceExecution.Current is not { IsCurrent: true } lease || !ReferenceEquals(lease.Owner, this)
            || lease.Origin.Trigger is not { } trigger) return null;
        var target = CompatibilityTarget(ConversationIdCodec.Encode(trigger.Conversation));
        return target.Id > 0 ? Resolve(target.IsGroup, target.Id, feature) : null;
    }

    public Task<IReadOnlyList<string>> RefreshImageUrlsAsync(long messageId, CancellationToken ct = default)
        => ResolveRead("image", messageId)?.RefreshImageUrlsAsync(messageId, ct)
            ?? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>上层登记会话用（见 <see cref="RegisterKey"/>）。</summary>
    public void RegisterTarget(string channel, bool isGroup, long id)
    {
        if (id > 0)
        {
            var ch = Channels.Declared(channel);
            if (string.IsNullOrEmpty(ch) || !_byChannel.ContainsKey(ch))
            {
                return;
            }
            _learned[(isGroup, id)] = ch;
            _byKey[Channels.Key(ch, isGroup, id)] = ch;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        foreach (var detach in _detachTrusted) detach();
        foreach (var src in _sources)
        {
            src.MessageReceived -= OnMessage;
            src.Poked -= OnPoked;
            src.MessageRecalled -= OnRecall;
            src.ConnectionChanged -= OnConnection;
            (src as IDisposable)?.Dispose();
        }
    }
}

/// <summary>
/// 「这台机器人有哪些上行通道」的只读台账：面板分块、状态条、`//status` 都从这里问。
/// 单通道部署时由网关自己实现（<see cref="IQqChatSource"/> 的默认实现就是“只有我自己”）。
/// </summary>
public interface IChannelRegistry
{
    IReadOnlyList<IQqChatSource> Sources { get; }

    IQqChatSource? Get(string channel);

    bool IsEnabled(string channel);
}
