using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S60 每日热备与恢复演练（#121）。
///
/// 为什么要有这个场景：<c>AppDatabase.VacuumIntoBackup</c> 早就写好了，但**全仓没有一处调用方**
/// —— 备份代码存在 ≠ 有备份。这个场景钉住的是一条真正能救命的链路：
///   ① 面板能当场热备，产物落在 data/backups；
///   ② 保留策略在真实调用链里生效（不是只在探针里）；
///   ③ 备份文件是**能被独立打开的数据库**（integrity_check=ok、user_version 与源库一致、
///      meta 行数一致）—— 只断言“文件存在”等于什么都没验证；
///   ④ 把备份放回一个新数据目录、用新进程起来，**数据真的还在**（改过的配置读回来还是那个值）。
///
/// 最后一条是重点：恢复演练要证明的是“能重启并继续用”，不是“文件没被删”。
/// </summary>
public static partial class Program
{
    private static async Task RunBackupRestoreScenarioAsync()
    {
        Section("S60 每日热备与恢复演练：备份能被独立打开、放回去能起来");

        var dataDir = NewDataDir("s60");
        var restoredDir = NewDataDir("s60-restore");
        var openAiPort = FreePort(17961);
        var botWsPort = FreePort(13161);
        var panelPort = FreePort(18161);
        var restoredPanelPort = FreePort(18162);

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        var backupDir = Path.Combine(dataDir, "data", "backups");
        string newest;

        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            using var bot = StartBot(new Dictionary<string, string>
            {
                ["QQCHAT_DATA_DIR"] = dataDir,
                ["QQCHAT_API_KEY"] = "sk-mock",
                ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
                ["QQCHAT_MODEL"] = "mock-model",
                ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
                ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
                ["QQCHAT_UIN"] = "10001",
                ["QQCHAT_WHITELIST"] = "66741",
                ["QQCHAT_AI_MODE"] = "1",
                ["QQCHAT_IDLE_FALLBACK"] = "0",
                ["QQCHAT_STICKERS"] = "0",
                ["QQCHAT_HEALTH_PORT"] = panelPort.ToString()
            });

            await WaitForPortAsync(botWsPort, cts.Token, bot);
            await WaitForPortAsync(panelPort, cts.Token, bot);

            // 先改一条行为配置当“恢复后还在不在”的标记：整库恢复最容易丢的就是这类面板写入的值。
            var (saveCode, saveBody) = await PanelPostJsonAsync(
                $"http://127.0.0.1:{panelPort}/api/settings", "{\"groupCooldownSeconds\":7}");
            Check("① 恢复演练前先在面板里改一条配置（拿它当恢复后的对照值）", saveCode == 200, saveBody);

            var (beforeCode, beforeBody) = await PanelGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
            Check("① 改动立刻可读（面板写入是真的落了库）",
                beforeCode == 200 && ReadSetting(beforeBody, "groupCooldownSeconds") == "7",
                Clip(beforeBody, 200));

            // ── ② 当场热备 4 次：保留策略必须只剩 3 份 ──
            var backupResults = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                var (code, body) = await PanelPostJsonAsync($"http://127.0.0.1:{panelPort}/api/backup", "{}");
                var node = JsonNode.Parse(body) as JsonObject;
                backupResults.Add($"{code}:{node?["ok"]}");
                await Task.Delay(20, cts.Token);
            }

            var files = Directory.Exists(backupDir)
                ? Directory.GetFiles(backupDir, "qqchat_daily_*.db").OrderBy(x => x, StringComparer.Ordinal).ToArray()
                : [];
            Check("② 面板能当场热备（每次调用都成功，产物落在 data/backups）",
                backupResults.All(r => r == "200:true") && files.Length > 0,
                string.Join(" | ", backupResults) + $" 文件 {files.Length} 份");

            Check("③ 保留策略在真实调用链里生效（连备 4 次只留 3 份）", files.Length == 3,
                $"实际 {files.Length} 份：{string.Join(", ", files.Select(Path.GetFileName))}");

            newest = files.Length > 0 ? files[^1] : string.Empty;

            var (listCode, listBody) = await PanelGetAsync($"http://127.0.0.1:{panelPort}/api/backup");
            var list = JsonNode.Parse(listBody) as JsonObject;
            Check("④ 面板能读到最近一次成功时间，且判定为未过期",
                listCode == 200 && !string.IsNullOrWhiteSpace(list?["lastSuccessAt"]?.ToString()) &&
                list?["stale"]?.GetValue<bool>() == false,
                Clip(listBody, 200));

            var (statusCode, statusBody) = await PanelGetAsync($"http://127.0.0.1:{panelPort}/status");
            var ops = (JsonNode.Parse(statusBody) as JsonObject)?["ops"] as JsonObject;
            Check("⑤ 状态接口带水位：库大小 / 日志目录大小 / 最近备份",
                statusCode == 200 &&
                (ops?["database"]?["bytes"]?.GetValue<long>() ?? 0) > 0 &&
                ops?["logs"]?["bytes"]?.GetValue<long>() >= 0 &&
                !string.IsNullOrWhiteSpace(ops?["backups"]?["lastSuccessAt"]?.ToString()),
                Clip(statusBody, 300));
        }

        // 进程停了之后再比对：源库带 WAL，跑着的时候只读打开需要 -shm 写权限，容易假红。
        var source = Path.Combine(dataDir, "data", "botagent.db");
        var sourceShape = InspectDatabase(source);
        var backupShape = InspectDatabase(newest);

        Check("⑥ 备份是能被独立打开的数据库（integrity_check = ok）",
            backupShape.Integrity == "ok", $"integrity={backupShape.Integrity}");

        Check("⑦ 备份的 schema 版本与源库一致（旧版二进制能不能读同一个库就看这个）",
            backupShape.Version == sourceShape.Version,
            $"源库 user_version={sourceShape.Version}（{sourceShape.Integrity}），备份={backupShape.Version}（{backupShape.Integrity}）");

        Check("⑧ 备份不是空壳：meta 行数与源库一致",
            backupShape.MetaRows == sourceShape.MetaRows && backupShape.MetaRows > 0,
            $"源库 meta {sourceShape.MetaRows} 行（{sourceShape.Integrity}），备份 {backupShape.MetaRows} 行");

        // ── ⑨ 真恢复：把备份放回一个全新的数据目录，用新进程起起来 ──
        var restoredDataDir = Path.Combine(restoredDir, "data");
        Directory.CreateDirectory(restoredDataDir);
        File.Copy(newest, Path.Combine(restoredDataDir, "botagent.db"), overwrite: true);

        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
        {
            var restoredWsPort = FreePort(13162);
            using var restored = StartBot(new Dictionary<string, string>
            {
                ["QQCHAT_DATA_DIR"] = restoredDir,
                ["QQCHAT_API_KEY"] = "sk-mock",
                ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
                ["QQCHAT_MODEL"] = "mock-model",
                ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
                ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{restoredWsPort}",
                ["QQCHAT_UIN"] = "10001",
                ["QQCHAT_WHITELIST"] = "66741",
                ["QQCHAT_AI_MODE"] = "1",
                ["QQCHAT_IDLE_FALLBACK"] = "0",
                ["QQCHAT_STICKERS"] = "0",
                ["QQCHAT_HEALTH_PORT"] = restoredPanelPort.ToString()
            });

            await WaitForPortAsync(restoredWsPort, cts.Token, restored);
            await WaitForPortAsync(restoredPanelPort, cts.Token, restored);

            var (healthCode, healthBody) = await PanelGetAsync($"http://127.0.0.1:{restoredPanelPort}/healthz");
            Check("⑨ 恢复出来的库能让新进程正常起来（/healthz 200，不是“库坏了就降级”）",
                healthCode == 200 && healthBody.Contains("\"ok\"", StringComparison.Ordinal),
                $"HTTP {healthCode} {Clip(healthBody, 120)}");

            var (restoredCode, restoredBody) = await PanelGetAsync($"http://127.0.0.1:{restoredPanelPort}/api/settings");
            Check("⑩ 恢复后数据真的还在：热备前改的配置读回来还是那个值",
                restoredCode == 200 && ReadSetting(restoredBody, "groupCooldownSeconds") == "7",
                $"HTTP {restoredCode} 值={ReadSetting(restoredBody, "groupCooldownSeconds") ?? "无"}");
        }
    }

    /// <summary>把回包截短到可读长度：断言失败时要贴出来，但别把整包配置糊进日志。</summary>
    private static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>从 /api/settings 的回包里取一个 runtime 字段（就是面板上能改的那个值）。</summary>
    private static string? ReadSetting(string body, string key)
    {
        try
        {
            return (JsonNode.Parse(body) as JsonObject)?["runtime"]?[key]?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>独立打开一个数据库文件看形状：能不能读、schema 版本多少、meta 有多少行。</summary>
    private static (string Integrity, int Version, long MetaRows) InspectDatabase(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return ("missing", -1, -1);
        }

        // 进程刚被杀掉时文件句柄还可能没放，SQLite 会短暂报 "database is locked" 或
        // "disk I/O error"（全量跑里才出现，单跑几乎不复现）。这里重试几次比调时序更稳：
        // 只读用途但 ReadWrite 打开，是为了让 SQLite 在源库（WAL）上重建 -shm。
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                    DefaultTimeout = 5
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA busy_timeout=5000;";
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA integrity_check;";
                var integrity = command.ExecuteScalar()?.ToString() ?? string.Empty;
                command.CommandText = "PRAGMA user_version;";
                var version = Convert.ToInt32(command.ExecuteScalar() ?? 0);
                command.CommandText = "SELECT COUNT(1) FROM meta;";
                var metaRows = Convert.ToInt64(command.ExecuteScalar() ?? 0L);
                return (integrity, version, metaRows);
            }
            catch (Exception exception)
            {
                last = exception;
                Thread.Sleep(300);
            }
        }

        return ($"{last?.GetType().Name}：{last?.Message}（已重试 6 次）", -1, -1);
    }
}
