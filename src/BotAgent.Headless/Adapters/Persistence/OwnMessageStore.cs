using System.IO;
using System.Text.Json;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Services;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 「机器人自己发出去的消息」台账（id → 原话 + 时间）：用来认出"别人引用回复了我说的哪一句"。
///
/// 为什么必须落库：以前只有内存表（上限 200、重启清空），而**每次部署都会重启** ——
/// 于是"引用机器人上一句"在部署后全部认不出来（管理员 2026-09-19 反馈被吞）。
/// 2026-09-22 从 <c>data/own-messages.json</c> 搬进 SQLite：那次是"每条都整份重写 JSON"，
/// 现在是单条 upsert + 按时间剪枝，既省事又不会再出现"崩一半、文件坏掉"。
///
/// 老文件的处置沿用 <see cref="LegacyJsonImporter" /> 的成例：**幂等导入 + 移到 legacy-json/ 留档**（不删）。
/// 过渡期兼容：文件不在、或已经导过 —— 都只是读不到旧数据，返回空表继续。
/// </summary>
public sealed class OwnMessageStore : IOwnMessageRepository
{
    /// <summary>导入完成标记。⚠ 用**独立**的 key：老部署早就有 <c>legacy_import_done</c> 了，
    /// 挂在那个标记下会让它们的 own-messages.json 永远导不进来。</summary>
    private const string ImportedKey = "own_messages_imported";

    private const string LegacyDirName = "legacy-json";

    /// <summary>台账最多留多少条（引旧消息的情况极少，200 条足够）。</summary>
    public int MaxEntries => MaxEntriesConst;

    /// <summary>上限的编译期常量（默认参数要用它，所以与上面的属性分开写）。</summary>
    public const int MaxEntriesConst = 200;

    /// <summary>
    /// 读出最近 <paramref name="max" /> 条（按时间倒序）。首次调用会先把老的 JSON 导进来（幂等）。
    /// </summary>
    public List<OwnMessage> LoadRecent(int max = MaxEntriesConst)
    {
        ImportIfNeeded();
        return AppDatabase.Query(
            "SELECT message_id, text, at_unix FROM own_messages ORDER BY at_unix DESC LIMIT $max",
            r => new OwnMessage(
                AppDatabase.Long(r, "message_id"),
                AppDatabase.Str(r, "text") ?? string.Empty,
                DateTimeOffset.FromUnixTimeSeconds(AppDatabase.Long(r, "at_unix"))),
            ("$max", max));
    }

    /// <summary>记一条（已存在就覆盖）。</summary>
    public void Upsert(long id, string text, DateTimeOffset at)
    {
        if (id <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return;   // 没拿到消息 id 的协议端：这条记不了，不是错误
        }

        AppDatabase.Write(conn => AppDatabase.Exec(conn,
            "INSERT INTO own_messages(message_id, text, at_unix) VALUES($id, $text, $at) " +
            "ON CONFLICT(message_id) DO UPDATE SET text = excluded.text, at_unix = excluded.at_unix",
            ("$id", id), ("$text", text), ("$at", at.ToUnixTimeSeconds())));
    }

    /// <summary>只留最近 <paramref name="max" /> 条（按时间）。</summary>
    public void PruneTo(int max = MaxEntriesConst)
    {
        AppDatabase.Write(conn => AppDatabase.Exec(conn,
            "DELETE FROM own_messages WHERE message_id NOT IN (" +
            "  SELECT message_id FROM own_messages ORDER BY at_unix DESC LIMIT $max)",
            ("$max", max)));
    }

    /// <summary>
    /// 把 <c>data/own-messages.json</c> 一次性导进来（幂等）。失败只记日志、不阻塞启动 ——
    /// 导不进来顶多是"部署后认不出引用"，机器人得照常能跑。
    /// </summary>
    private void ImportIfNeeded()
    {
        try
        {
            if (AppDatabase.HasMeta(ImportedKey))
            {
                return;
            }

            var path = Path.Combine(AppPaths.DataDir, "own-messages.json");
            var imported = 0;
            if (File.Exists(path))
            {
                var items = new List<(long Id, string Text, DateTimeOffset At)>();
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idNode) ? idNode.GetInt64() : 0;
                        var text = item.TryGetProperty("text", out var textNode) ? textNode.GetString() : null;
                        // 旧数据没写 at 时按"最早"处理：只影响它被剪枝的先后，不影响内容可读
                        var at = item.TryGetProperty("at", out var atNode) && atNode.TryGetDateTimeOffset(out var parsed)
                            ? parsed
                            : DateTimeOffset.UnixEpoch;
                        if (id > 0 && !string.IsNullOrWhiteSpace(text))
                        {
                            items.Add((id, text!, at));
                        }
                    }
                }

                AppDatabase.Write(conn =>
                {
                    foreach (var (id, text, at) in items)
                    {
                        AppDatabase.Exec(conn,
                            "INSERT INTO own_messages(message_id, text, at_unix) VALUES($id, $text, $at) " +
                            "ON CONFLICT(message_id) DO UPDATE SET text = excluded.text, at_unix = excluded.at_unix",
                            ("$id", id), ("$text", text), ("$at", at.ToUnixTimeSeconds()));
                    }
                });
                imported = items.Count;
                Archive(path);
            }

            // 时间戳让 SQLite 自己取（这里是 IO 层，不该再去读系统时钟）
            AppDatabase.Write(conn => AppDatabase.Exec(conn,
                "INSERT INTO meta(key, value) VALUES($key, strftime('%Y-%m-%dT%H:%M:%SZ','now')) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                ("$key", ImportedKey)));
            if (imported > 0)
            {
                FileLog.Write("DB", $"自己发过的消息已入库 {imported} 条，旧文件已移到 {LegacyDirName}/ 留档");
            }
        }
        catch (Exception ex)
        {
            // 当空表继续：认不出引用总比起不来强（下次启动还会重试）
            FileLog.Warn("DB", $"own-messages.json 导入失败（当空表继续）：{ex.Message}");
        }
    }

    /// <summary>把导完的旧文件移到 legacy-json/ 下（与 <see cref="LegacyJsonImporter" /> 同一约定：留档、不删）。</summary>
    private static void Archive(string path)
    {
        var legacy = Path.Combine(AppPaths.RuntimeRoot, LegacyDirName, "data");
        Directory.CreateDirectory(legacy);
        File.Move(path, Path.Combine(legacy, Path.GetFileName(path)), overwrite: true);
    }
}
