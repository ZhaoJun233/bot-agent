using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Conversations;

/// <summary>
/// 会话注册表（用例层）：内存里的会话列表 + 排列顺序 + 落库 / 恢复。
///
/// 三条不变量（重构时别弄丢，任何一条破了都会在线上看得见）：
///   ① 列表的读写一律在 <c>_gate</c> 里；删会话时先把名字取出来，再离开锁做收尾；
///   ② 写盘是后台合并循环（<c>_saveVersion</c> + <c>_savePending</c>）：**不在接收线程上做深拷贝** ——
///      以前这里在 WS 接收线程上同步 <c>ToRecord()</c>，消息一多就直接卡住收消息；
///   ③ 白名单只管“要不要回”，**不管“存不存”**（2026-09-21 的语义修正）：
///      恢复时按名单过滤，运行期绝不因为“不在白名单”就把会话从内存或库里删掉 —— 那会让面板里的会话凭空消失。
/// </summary>
public sealed class ConversationRegistry
{
    private readonly IConversationRepository _store;
    private readonly SettingsBox _box;
    private readonly IQqChatSource _source;
    private readonly Func<string, bool> _isWhitelistedKey;
    private readonly Action<string> _log;

    private readonly object _gate = new();
    private readonly List<BotConversation> _conversations = new();
    private int _savePending;
    private long _saveVersion;

    public ConversationRegistry(
        IConversationRepository store,
        SettingsBox box,
        IQqChatSource source,
        Func<string, bool> isWhitelistedKey,
        Action<string> log)
    {
        _store = store;
        _box = box;
        _source = source;
        _isWhitelistedKey = isWhitelistedKey;
        _log = log;
    }

    private AppSettings _settings => _box.Current;

    /// <summary>会话发生变更（新建/删除/未读/顺序）—— 面板据此刷新会话列表。</summary>
    public event Action? Changed;

    /// <summary>
    /// 会话**刚被删掉**（已摘出内存、库里那两行也删了）：宿主在这里清它在各台账里的痕迹
    /// （回复冷却 / 待回复队列 / 参与台账 / 审批台账）。
    /// 为什么挂在注册表上、而不是让每个调用方自己补一遍：删会话是"注册表 + 那几个台账"一起的事，
    /// 散到调用方就会漏 —— 面板删了会话、台账还留着，面板「参与状态」里就能看见幽灵会话。
    /// </summary>
    public event Action<BotConversation>? Deleted;

    /// <summary>已跟踪的会话快照（按最后活跃时间倒序）。</summary>
    public IReadOnlyList<BotConversation> Snapshot()
    {
        lock (_gate)
        {
            return _conversations.ToArray();
        }
    }

    /// <summary>按 sourceKey 查找会话。</summary>
    public BotConversation? Find(string sourceKey)
    {
        lock (_gate)
        {
            return _conversations.FirstOrDefault(c => c.SourceKey == sourceKey);
        }
    }

    /// <summary>
    /// 按入站消息取会话（没有就新建）。
    /// 通道前缀在 <see cref="Channels.Key" /> 里加（官方 = <c>official:group:123</c>；私域保持老格式
    /// <c>group:123</c>）—— 前缀就是隔离：官方那边的 openid 与私域的真实 QQ 号哪怕数字碰上，也是两个会话。
    /// </summary>
    public BotConversation GetOrCreate(QqChatMessage msg)
    {
        var channel = Channels.Declared(msg.Channel);
        var key = Channels.Key(channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);
        _source.RegisterTarget(channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);

        lock (_gate)
        {
            var existing = _conversations.FirstOrDefault(c => c.SourceKey == key);
            if (existing is not null)
            {
                return existing;
            }

            var name = msg.IsGroup
                ? $"{Channels.Tag(channel)}群聊 {msg.GroupId}"
                : (msg.SenderName ?? msg.UserId.ToString());
            var conversation = new BotConversation
            {
                SourceKey = key,
                Kind = msg.IsGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat,
                Name = name,
                MaxMessages = _settings.MaxMessagesPerConversation
            };
            conversation.OnEvicted = ArchiveEvicted;
            _conversations.Add(conversation);
            _log($"新建会话 {name} ({key})");
            Changed?.Invoke();
            return conversation;
        }
    }

    /// <summary>更新活跃时间并重排（最近活跃在前）。</summary>
    public void Touch(BotConversation conversation)
    {
        lock (_gate)
        {
            _conversations.Remove(conversation);
            _conversations.Insert(0, conversation);
        }

        Changed?.Invoke();
    }

    /// <summary>删会话：从内存摘掉 + 显式删库（平时的保存只 upsert，不删任何东西）。返回被删的那个（没有则 null）。</summary>
    public BotConversation? Delete(string sourceKey)
    {
        BotConversation? conversation;
        lock (_gate)
        {
            conversation = _conversations.FirstOrDefault(c => c.SourceKey == sourceKey);
            if (conversation is null)
            {
                return null;
            }

            _conversations.Remove(conversation);
        }

        _store.DeleteConversation(sourceKey);
        Save();
        Changed?.Invoke();
        Deleted?.Invoke(conversation);
        return conversation;
    }

    /// <summary>标记会话已读（真的从“未读”变成“已读”时才通知）。</summary>
    public void MarkRead(string sourceKey)
    {
        var conversation = Find(sourceKey);
        if (conversation?.MarkRead() == true)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// 启动时把库里的会话读回内存。
    /// 只恢复白名单里的：不在名单的会话即使库里有也不进上下文（入站那一道已经拦了，恢复再放进内存只会白占）。
    /// </summary>
    public void Restore()
    {
        try
        {
            var records = _store.LoadAsync();
            if (records.Count == 0)
            {
                return;
            }

            var restored = 0;
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.SourceKey) || !_isWhitelistedKey(record.SourceKey))
                {
                    continue;
                }

                lock (_gate)
                {
                    if (_conversations.Any(c => c.SourceKey == record.SourceKey))
                    {
                        continue;
                    }

                    var restoredConv = BotConversation.FromRecord(record, _settings.MaxMessagesPerConversation, ArchiveEvicted);
                    _conversations.Add(restoredConv);

                    // 告诉聚合器这条会话属于哪条通道：重启后面板代发、主动消息都得靠它选对通道
                    var (restoredIsGroup, restoredId) = restoredConv.Target;
                    if (restoredId > 0)
                    {
                        _source.RegisterTarget(restoredConv.Channel, restoredIsGroup, restoredId);
                    }
                }

                restored++;
            }

            lock (_gate)
            {
                _conversations.Sort((a, b) => b.LastTime.CompareTo(a.LastTime));
            }

            FileLog.Write("Store", $"已恢复 {restored} 个会话");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            FileLog.Write("Store", "恢复会话失败: " + ex.Message);
        }
    }

    /// <summary>请求保存（合并窗口 + 版本号重检，见 <see cref="SaveLoopAsync" />）。</summary>
    public void Save()
    {
        Interlocked.Increment(ref _saveVersion);

        if (Interlocked.Exchange(ref _savePending, 1) == 1)
        {
            return; // 已有保存任务在跑，它会带上本次变更
        }

        _ = SaveLoopAsync();
    }

    /// <summary>
    /// 持久化循环（后台线程）。
    /// 以前这个在**WS 接收线程上同步**执行，而且 ToRecord() 是对所有会话做深拷贝 ——
    /// 消息一多就会直接阻塞收消息。现在改成后台任务 + 合并窗口 + 版本号重检。
    /// </summary>
    private async Task SaveLoopAsync()
    {
        try
        {
            while (true)
            {
                var version = Interlocked.Read(ref _saveVersion);

                await Clock.Delay(150); // 合并窗口：短时间内的多次变更只写一次盘

                List<ConversationRecord> records;
                lock (_gate)
                {
                    records = _conversations.Select(c => c.ToRecord()).ToList();
                }

                _store.RequestSave(records);

                // 先放行，再检查期间是否有新变更 ——
                // 顺序不能反，否则会在临界区漏掉最后一次保存。
                Interlocked.Exchange(ref _savePending, 0);

                if (Interlocked.Read(ref _saveVersion) == version)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _savePending, 1) == 1)
                {
                    return; // 已被其他调用接手
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _savePending, 0);
            FileLog.Write("Store", "保存会话失败: " + ex.Message);
        }
    }

    /// <summary>把超出滚动窗口的旧消息追加进归档（同一张表 archived=1），不丢历史但也不占内存。</summary>
    private void ArchiveEvicted(string sourceKey, IReadOnlyList<ChatMessage> evicted)
    {
        // 归档进库（messages 表 archived=1），不再另开 .jsonl 文件：
        // 面板翻旧账、按时间/关键词查、以后做自动清理都是一句 SQL。
        try
        {
            _store.AppendArchive(sourceKey, evicted);
        }
        catch (Exception ex)
        {
            FileLog.Write("Archive", "归档失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 这个会话里出现过的**名字**（≥2 字，去重）—— 显示层脱敏要用它做整词替换
    /// （把"张三"这样出现过的名字也一起遮掉，不只是遮 QQ 号）。
    /// </summary>
    public List<string> KnownNames(string sourceKey)
    {
        var names = new List<string>();
        var conversation = Find(sourceKey);
        if (conversation is not null)
        {
            names.AddRange(conversation.Messages
                .Where(m => m.SenderName is { Length: >= 2 })
                .Select(m => m.SenderName!));
        }

        return names.Distinct().ToList();
    }

    /// <summary>面板用：读某会话的归档（已溢出滚动窗口的旧消息，最新在前）。</summary>
    public List<ArchivedMessage> ReadArchive(string sourceKey, int limit)
        => _store.ReadArchive(sourceKey, limit);
}
