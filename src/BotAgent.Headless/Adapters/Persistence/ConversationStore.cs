using System.Text.Encodings.Web;
using System.Text.Json;
using BotAgent.Services;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 会话与消息的持久化（SQLite：<c>conversations</c> + <c>messages</c> 两张表）。
///
/// 与老 JSON 版的区别（为什么换）：
///   • JSON 版是**整份重写**（几千条消息时每次都序列化全部内容），现在是按会话增量 upsert；
///   • 归档从“另开一堆 .jsonl 文件”改成同一张表里 <c>archived=1</c> 的行 ——
///     翻旧账、按时间查、清理都变成一句 SQL；
///   • 撤回只是把 <c>recalled</c> 改成 1（不删行），符合“内容保留但标出来”的语义。
/// 对外 API 保持原样（LoadAsync / RequestSave），上层不用改。
/// </summary>
public sealed class ConversationStore : IConversationRepository
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>加载全部会话（不含归档消息；归档由面板的 /api/archive 单独查）。</summary>
    public List<ConversationRecord> LoadAsync()
    {
        try
        {
            return LoadCore();
        }
        catch (Exception ex)
        {
            FileLog.Warn("Store", "读取会话失败（按空处理）：" + ex.Message);
            return new List<ConversationRecord>();
        }
    }

    /// <summary>同步加载（关停刷盘等场景用）。</summary>

    private static List<ConversationRecord> LoadCore()
    {
        {
            var conversations = AppDatabase.Query("""
                SELECT id, source_key, kind, name, avatar_text, avatar_url, avatar_index,
                       last_time_unix, unread_count, next_seq
                FROM conversations
                ORDER BY last_time_unix DESC
                """, r => new ConversationRecord
            {
                Id = AppDatabase.Str(r, "id"),
                SourceKey = AppDatabase.Str(r, "source_key"),
                Kind = AppDatabase.Str(r, "kind") ?? "GroupChat",
                Name = AppDatabase.Str(r, "name") ?? string.Empty,
                AvatarText = AppDatabase.Str(r, "avatar_text") ?? "?",
                AvatarUrl = AppDatabase.Str(r, "avatar_url"),
                AvatarIndex = AppDatabase.Int(r, "avatar_index"),
                LastTimeUnix = AppDatabase.Long(r, "last_time_unix"),
                UnreadCount = AppDatabase.Int(r, "unread_count"),
                NextSeq = AppDatabase.Long(r, "next_seq")
            });

            // 消息一次查完再按会话分：避免“会话数 × 一次查询”的 N+1
            var byKey = AppDatabase.Query("""
                SELECT source_key, seq, role, text, time_unix, sender_name, sender_id, qq_message_id, recalled, images
                FROM messages
                WHERE archived = 0
                ORDER BY source_key, seq
                """, r => (
                    Key: AppDatabase.Str(r, "source_key") ?? string.Empty,
                    Msg: new MessageRecord
                    {
                        Seq = AppDatabase.Long(r, "seq"),
                        Role = AppDatabase.Str(r, "role") ?? "Peer",
                        Text = AppDatabase.Str(r, "text") ?? string.Empty,
                        TimeUnix = AppDatabase.Long(r, "time_unix"),
                        SenderName = AppDatabase.Str(r, "sender_name"),
                        SenderId = AppDatabase.LongOrNull(r, "sender_id"),
                        QqMessageId = AppDatabase.LongOrNull(r, "qq_message_id"),
                        Recalled = AppDatabase.Bool(r, "recalled"),
                        ImageUrls = ParseImages(AppDatabase.Str(r, "images"))
                    })).ToLookup(x => x.Key, x => x.Msg);

            foreach (var conversation in conversations)
            {
                var key = conversation.SourceKey ?? string.Empty;
                if (byKey.Contains(key))
                {
                    conversation.Messages = byKey[key].ToList();
                }
            }

            return conversations;
        }
    }

    /// <summary>
    /// 请求保存。
    ///
    /// 这里故意同步等待存储锁：调用方是注册表的后台保存循环，只有等这次快照真正写完，
    /// 清空/删除操作才能在后续拿到同一把锁并保证不会被旧快照重新写回来。
    /// </summary>
    public void RequestSave(IEnumerable<ConversationRecord> records)
    {
        SaveCoreAsync(records.ToList()).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 写库（同步，但跑在线程池上）。
    /// 这里**不做 1 秒节流延迟**：上层 BotAgentHost.SaveLoopAsync 已经有 150ms 合并窗口，
    /// 而额外那 1 秒会把“刚写进来的消息”拖到进程可能已经被杀之后 ——
    /// 线上/测试都验过：重启后活动消息丢空，然后序号从 0 重开、把老消息覆盖掉。
    /// </summary>
    private async Task SaveCoreAsync(List<ConversationRecord> records)
    {
        await _lock.WaitAsync();
        try
        {
            await AppDatabase.WriteAsync(conn =>
            {
                foreach (var c in records)
                {
                    var key = string.IsNullOrWhiteSpace(c.SourceKey) ? $"local:{c.Id}" : c.SourceKey!;
                    AppDatabase.Exec(conn, """
                        INSERT INTO conversations(id, source_key, kind, name, avatar_text, avatar_url, avatar_index,
                                                  last_time_unix, unread_count, history_loaded, max_messages, next_seq, updated_unix)
                        VALUES($id, $key, $kind, $name, $at, $au, $ai, $lt, $uc, 0, 500, $next, $now)
                        ON CONFLICT(source_key) DO UPDATE SET
                            name = excluded.name, avatar_text = excluded.avatar_text, avatar_url = excluded.avatar_url,
                            avatar_index = excluded.avatar_index, last_time_unix = excluded.last_time_unix,
                            unread_count = excluded.unread_count, next_seq = excluded.next_seq,
                            updated_unix = excluded.updated_unix
                        """,
                        ("$id", string.IsNullOrWhiteSpace(c.Id) ? Guid.NewGuid().ToString("N") : c.Id!),
                        ("$key", key),
                        ("$kind", c.Kind),
                        ("$name", c.Name ?? string.Empty),
                        ("$at", c.AvatarText),
                        ("$au", c.AvatarUrl),
                        ("$ai", c.AvatarIndex),
                        ("$lt", c.LastTimeUnix),
                        ("$uc", c.UnreadCount),
                        ("$next", c.NextSeq),
                        ("$now", Clock.Now.ToUnixTimeSeconds()));

                    // 消息：**只 upsert，不删**。
                    // 为什么：保存是异步的、快照可能比另一次保存旧（“较晚入队、较早执行”），
                    // 一旦按快照做 DELETE，就会把对方刚写进去的新消息删掉 —— 线上验过：
                    // 重启后整会话的活消息被清空（陈旧快照里消息少）。
                    // 从活动列表里“消失”的语义由 archived 标记表达（滚动窗口淘汰 → AppendArchive），
                    // 真要删就走 DeleteConversation（面板删会话）。
                    foreach (var m in c.Messages)
                    {
                        AppDatabase.Exec(conn, """
                            INSERT INTO messages(source_key, seq, role, text, time_unix, sender_name, sender_id,
                                                 qq_message_id, recalled, images, archived)
                            VALUES($key, $seq, $role, $text, $t, $sn, $sid, $mid, $rec, $img, 0)
                            ON CONFLICT(source_key, seq) DO UPDATE SET
                                role = excluded.role, text = excluded.text, time_unix = excluded.time_unix,
                                sender_name = excluded.sender_name, sender_id = excluded.sender_id,
                                qq_message_id = excluded.qq_message_id, recalled = excluded.recalled,
                                images = excluded.images
                            """,
                            ("$key", key), ("$seq", m.Seq), ("$role", m.Role), ("$text", m.Text),
                            ("$t", m.TimeUnix), ("$sn", m.SenderName), ("$sid", m.SenderId),
                            ("$mid", m.QqMessageId), ("$rec", m.Recalled ? 1 : 0),
                            ("$img", m.ImageUrls is { Count: > 0 } ? JsonSerializer.Serialize(m.ImageUrls) : null));
                    }
                }
            });
        }
        catch (Exception ex)
        {
            FileLog.Warn("Store", "保存会话失败：" + ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>删掉某会话（面板删会话时显式调用；平时保存不会删任何消息）。</summary>
    public void DeleteConversation(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return;
        }

        _lock.Wait();
        try
        {
            AppDatabase.Write(conn =>
            {
                AppDatabase.Exec(conn, "DELETE FROM messages WHERE source_key = $key", ("$key", sourceKey));
                AppDatabase.Exec(conn, "DELETE FROM conversations WHERE source_key = $key", ("$key", sourceKey));
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>把被滚动窗口挤出去的消息写进归档（同一张表，archived=1）。</summary>
    public void AppendArchive(string sourceKey, IReadOnlyList<ChatMessage> evicted)
    {
        if (string.IsNullOrEmpty(sourceKey) || evicted.Count == 0)
        {
            return;
        }

        _lock.Wait();
        try
        {
            AppDatabase.Write(conn =>
            {
                foreach (var m in evicted)
                {
                    AppDatabase.Exec(conn, """
                        INSERT INTO messages(source_key, seq, role, text, time_unix, sender_name, sender_id,
                                             qq_message_id, recalled, images, archived)
                        VALUES($key, $seq, $role, $text, $t, $sn, $sid, $mid, $rec, $img, 1)
                        ON CONFLICT(source_key, seq) DO UPDATE SET archived = 1
                        """,
                        ("$key", sourceKey), ("$seq", SeqOf(m)), ("$role", m.Role.ToString()), ("$text", m.Text),
                        ("$t", m.Timestamp.ToUnixTimeSeconds()), ("$sn", m.SenderName), ("$sid", m.SenderId),
                        ("$mid", m.QqMessageId), ("$rec", m.Recalled ? 1 : 0),
                        ("$img", m.ImageUrls is { Count: > 0 } ? JsonSerializer.Serialize(m.ImageUrls) : null));
                }
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>读某会话的归档尾部（面板“翻旧账”用）。</summary>
    public List<ArchivedMessage> ReadArchive(string sourceKey, int limit)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return new List<ArchivedMessage>();
        }

        try
        {
            return AppDatabase.Query("""
                SELECT seq, role, text, time_unix, sender_name, sender_id, qq_message_id, recalled
                FROM messages
                WHERE source_key = $key AND archived = 1
                ORDER BY seq DESC
                LIMIT $limit
                """, r => new ArchivedMessage(
                    AppDatabase.Long(r, "seq"),
                    AppDatabase.Str(r, "role") ?? "Peer",
                    AppDatabase.Str(r, "text") ?? string.Empty,
                    AppDatabase.Long(r, "time_unix"),
                    AppDatabase.Str(r, "sender_name"),
                    AppDatabase.LongOrNull(r, "sender_id"),
                    AppDatabase.LongOrNull(r, "qq_message_id"),
                    AppDatabase.Bool(r, "recalled")),
                ("$key", sourceKey), ("$limit", Math.Clamp(limit, 1, 2000)));
        }
        catch (Exception ex)
        {
            FileLog.Warn("Store", "读取归档失败：" + ex.Message);
            return new List<ArchivedMessage>();
        }
    }

    /// <summary>归档总条数（面板显示用）。</summary>
    public long ArchiveCount(string sourceKey)
        => AppDatabase.Scalar<long>("SELECT COUNT(1) FROM messages WHERE source_key = $key AND archived = 1", ("$key", sourceKey));

    /// <summary>删掉某会话的全部活动与归档消息（面板清空历史时用）。</summary>
    public void DeleteMessages(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return;
        }

        _lock.Wait();
        try
        {
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "DELETE FROM messages WHERE source_key = $key", ("$key", sourceKey)));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>ChatMessage 没有公开 Seq，这里从“内部序号”取；取不到时用时间戳兜底（负数段，避免撞车）。</summary>
    private static long SeqOf(ChatMessage m)
        => m.Seq != 0 ? m.Seq : -Math.Abs(m.Timestamp.ToUnixTimeMilliseconds());

    private static List<string>? ParseImages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
