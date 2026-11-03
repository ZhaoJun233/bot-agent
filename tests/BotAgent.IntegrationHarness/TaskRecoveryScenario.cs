using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S65：任务台账跨重启（#116）。上一进程遗留的非终态任务，重启后必须收口成可解释的终态，
/// 并且绝不能被重做：Queued/WaitingApproval → Cancelled(cancelled_before_dispatch)，
/// Running/CancelRequested → Unknown(restart_state_unknown)。
/// </summary>
public static partial class Program
{
    private static async Task RunTaskRecoveryScenarioAsync()
    {
        Section("S65 任务台账跨重启（Queued→Cancelled / Running→Unknown / 不重做）");

        const string adminToken = "synthetic-s65-admin";
        const string readToken = "synthetic-s65-read";
        var dataDir = NewDataDir("s65");
        var panel1 = FreePort(18130);
        var ws1 = FreePort(13080);
        var panel2 = FreePort(18131);
        var ws2 = FreePort(13081);
        const string runningId = "zt-s65-running";
        const string queuedId = "zt-s65-queued";

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        Dictionary<string, string> Env(int panelPort, int wsPort) => new()
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "synthetic-task-recovery-key",
            ["QQCHAT_BASE_URL"] = "http://127.0.0.1:1/v1",
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{wsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "99999",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = SyntheticPanelToken,
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken,
        };

        // 第一次启动只用来把库和 schema 建出来（任务台账表随 v12 迁移创建）。
        using (var bot = StartBot(Env(panel1, ws1)))
        {
            await WaitForPortAsync(panel1, cts.Token, bot);
            // 必须显式 StopAsync：Dispose 只 Kill 不等待，进程还在跑的时候去写库会拿到
            // 「SQLite Error 10: disk I/O error」（全量跑时踩到过，单跑不复现）。
            await bot.StopAsync();
        }

        var dbPath = Path.Combine(dataDir, "data", "botagent.db");
        Check("S65 首次启动建出库文件", File.Exists(dbPath), dbPath);

        // 直接注入两条"上一个进程留下"的行：一条 Running、一条 Queued。
        var seededAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz");
        var seededUpdated = DateTimeOffset.UtcNow.AddMinutes(-4).ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz");
        var seeded = 0;
        string? seedError = null;
        // 进程刚被杀掉时文件句柄可能还没放，写库偶发 disk I/O error → 有界重试。
        for (var attempt = 0; attempt < 10 && seeded != 2; attempt++)
        {
            seeded = 0;
            try
            {
                using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false
                }.ToString());
                conn.Open();
                using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO agent_tasks
                        (id, state, error_code, source_key, session_id, target, created_at, updated_at,
                         duration_ms, tool_calls, cancel_requested, result_chars, result_truncated, boot_seq)
                    VALUES ($id, $state, $error, $source, $session, $target, $created, $updated,
                            0, 0, 0, 0, 0, 0);
                    """;
                foreach (var (id, state) in new[] { (runningId, 1), (queuedId, 0) })
                {
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.Parameters.AddWithValue("$state", state);
                    cmd.Parameters.AddWithValue("$error", "seed");
                    cmd.Parameters.AddWithValue("$source", "panel:s65");
                    cmd.Parameters.AddWithValue("$session", "s65-session");
                    cmd.Parameters.AddWithValue("$target", "server");
                    cmd.Parameters.AddWithValue("$created", seededAt);
                    cmd.Parameters.AddWithValue("$updated", seededUpdated);
                    seeded += cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                seedError = ex.Message;
                seeded = 0;
                await Task.Delay(300, cts.Token);
            }
        }

        Check("S65 能写入上一进程遗留的任务行", seeded == 2, "rows=" + seeded + (seedError is null ? "" : " err=" + seedError));

        using (var bot = StartBot(Env(panel2, ws2)))
        {
            await WaitForPortAsync(ws2, cts.Token, bot);
            await WaitForPortAsync(panel2, cts.Token, bot);
            var root = $"http://127.0.0.1:{panel2}/zba/v1";

            var running = await ZbaAsync(http, "GET", root + "/tasks/" + runningId, adminToken);
            var runningNode = JsonNode.Parse(running.Body) as JsonObject;
            Check("S65 重启前在运行的任务恢复成 Unknown 且写明原因",
                running.Status == 200
                && runningNode?["state"]?.GetValue<int>() == 7
                && runningNode?["errorCode"]?.GetValue<string>() == "restart_state_unknown",
                running.Status + " " + running.Body);

            var queued = await ZbaAsync(http, "GET", root + "/tasks/" + queuedId, adminToken);
            var queuedNode = JsonNode.Parse(queued.Body) as JsonObject;
            Check("S65 重启前未开跑的任务恢复成 Cancelled 且写明原因",
                queued.Status == 200
                && queuedNode?["state"]?.GetValue<int>() == 6
                && queuedNode?["errorCode"]?.GetValue<string>() == "cancelled_before_dispatch",
                queued.Status + " " + queued.Body);

            var list = await ZbaAsync(http, "GET", root + "/tasks?limit=50", adminToken);
            var listNode = JsonNode.Parse(list.Body) as JsonObject;
            var ids = listNode?["items"]?.AsArray()
                .Select(item => item?["id"]?.GetValue<string>())
                .Where(id => id is not null)
                .ToArray() ?? Array.Empty<string?>();
            Check("S65 恢复的任务出现在任务列表里",
                list.Status == 200 && ids.Contains(runningId) && ids.Contains(queuedId),
                list.Status + " ids=" + string.Join(",", ids));

            // 不重做：再查一次状态不变（若被重放会重新排队/运行，甚至 409）。
            var again = await ZbaAsync(http, "GET", root + "/tasks/" + runningId, adminToken);
            var againNode = JsonNode.Parse(again.Body) as JsonObject;
            Check("S65 恢复的任务不会被重做（状态停在 Unknown）",
                again.Status == 200 && againNode?["state"]?.GetValue<int>() == 7,
                again.Status + " " + again.Body);
        }
    }
}
