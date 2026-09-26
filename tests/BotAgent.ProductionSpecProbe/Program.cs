using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Ops;
using BotAgent.Services.Ops;
using BotAgent.Services;

namespace BotAgent.ProductionSpecProbe;

public static class Program
{
    public static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-production-spec-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);

        try
        {
            AppDatabase.Initialize();
            SettingsExistenceTests();
            AuditChainTests();
            TraceArchiveTests();
            Console.WriteLine("通过 8，失败 0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败 1：" + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // 测试目录清理失败不应覆盖真正的断言结果。
            }
        }
    }

    private static void SettingsExistenceTests()
    {
        var settings = new SettingsStore();
        Check(!settings.HasStoredSettings(), "没有配置行时使用首次部署的环境种子");

        // 空 JSON 值也是一条真实配置行，存在性不应依赖 JSON 内容。
        AppDatabase.Write(conn => AppDatabase.Exec(conn,
            "INSERT INTO settings(id, json, updated_unix) VALUES(1, '', 0)"));
        Check(settings.HasStoredSettings(), "空 JSON 值仍被视为已保存配置");

        settings.Save(new AppSettings());
        Check(settings.HasStoredSettings(), "正常保存后仍能检测已有配置");
    }
    private static void AuditChainTests()
    {
        var audit = new AuditLogStore();
        audit.Append(new AuditEvent("config_change", "synthetic-panel", "synthetic:tenant-a",
            "{\"result\":\"applied\",\"fieldCount\":1}", "2.1"));
        audit.Append(new AuditEvent("gate_block", "synthetic-agent", "synthetic:tenant-a",
            "{\"result\":\"blocked\",\"reason\":\"not_allowlisted\"}", "2.1"));

        var valid = audit.Verify();
        Check(valid.Valid && valid.CheckedCount == 2, "审计链追加后可完整校验");

        AppDatabase.Write(conn => AppDatabase.Exec(conn,
            "UPDATE security_audit_log SET action_detail = $detail WHERE id = 2",
            ("$detail", "{\"result\":\"tampered\"}")));
        var broken = audit.Verify();
        Check(!broken.Valid && broken.BreakpointId == 2 && broken.ErrorType == "curr_hash_mismatch",
            "审计链能报告被篡改的断点");
    }

    private static void TraceArchiveTests()
    {
        var trace = new TurnTrace(
            "synthetic-trace-1",
            "synthetic:tenant-a",
            DateTimeOffset.UtcNow,
            "timeout",
            5001,
            new[]
            {
                new TurnNode(TurnNodeKind.Model, "timeout", 5001, ReasonCode: "upstream_timeout")
            });

        var archive = new TraceArchiveStore();
        archive.Append(trace);
        var first = archive.Snapshot();
        Check(first.Count == 1 && first.Durations.SequenceEqual(new[] { 5001 }),
            "慢轨迹写入后 /metrics 形状可查询");

        var restartedView = new TraceArchiveStore().Snapshot();
        Check(restartedView.Count == 1 && restartedView.Durations.SequenceEqual(new[] { 5001 }),
            "重新构造归档适配器后仍能读取 SQLite 数据");

        Check(first.PromptTokens == 0 && first.CompletionTokens == 0,
            "无 token 计数时输出安全默认值 0");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name);
        }
    }
}
