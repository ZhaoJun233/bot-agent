using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Prompts;
using BotAgent.Services;
using Microsoft.Data.Sqlite;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// Prompt 模板版本快照 SQLite 持久化仓储（对标 MaiBot Prompt 版本化与出厂回滚机制）。
/// </summary>
public class PromptTemplateStore : IPromptTemplateRepository
{
    public Task<IReadOnlyList<PromptTemplateSnapshot>> ListVersionsAsync(string key)
    {
        var list = AppDatabase.Query(
            "SELECT id, key, version_id, content, label, is_active, created_unix " +
            "FROM prompt_templates WHERE key = $k ORDER BY id DESC",
            Map,
            ("$k", key));

        return Task.FromResult<IReadOnlyList<PromptTemplateSnapshot>>(list);
    }

    public Task<PromptTemplateSnapshot?> GetVersionAsync(string key, string versionId)
    {
        var list = AppDatabase.Query(
            "SELECT id, key, version_id, content, label, is_active, created_unix " +
            "FROM prompt_templates WHERE key = $k AND version_id = $v LIMIT 1",
            Map,
            ("$k", key), ("$v", versionId));

        return Task.FromResult(list.FirstOrDefault());
    }

    public Task<PromptTemplateSnapshot?> GetActiveVersionAsync(string key)
    {
        var list = AppDatabase.Query(
            "SELECT id, key, version_id, content, label, is_active, created_unix " +
            "FROM prompt_templates WHERE key = $k AND is_active = 1 LIMIT 1",
            Map,
            ("$k", key));

        return Task.FromResult(list.FirstOrDefault());
    }

    public Task<string> SaveVersionAsync(string key, string content, string? label = null)
    {
        var now = Clock.Now.ToUnixTimeSeconds();
        var newVersion = string.Empty;

        AppDatabase.Write(conn =>
        {
            // 版本分配与取消激活、插入共用写事务，避免并发保存分配到同一个版本号。
            using var countCommand = conn.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(1) FROM prompt_templates WHERE key = $k";
            countCommand.Parameters.AddWithValue("$k", key);
            newVersion = $"v{Convert.ToInt64(countCommand.ExecuteScalar()) + 1}";

            // 将旧版本激活状态取消
            AppDatabase.Exec(conn, "UPDATE prompt_templates SET is_active = 0 WHERE key = $k", ("$k", key));

            // 插入新版本并设为激活
            AppDatabase.Exec(conn,
                "INSERT INTO prompt_templates(key, version_id, content, label, is_active, created_unix) " +
                "VALUES($k, $v, $c, $l, 1, $u)",
                ("$k", key), ("$v", newVersion), ("$c", content), ("$l", label), ("$u", now));
        });

        return Task.FromResult(newVersion);
    }

    public Task<bool> ActivateVersionAsync(string key, string versionId)
    {
        var exists = AppDatabase.Scalar<long>(
            "SELECT COUNT(1) FROM prompt_templates WHERE key = $k AND version_id = $v",
            ("$k", key), ("$v", versionId)) > 0;

        if (!exists)
        {
            return Task.FromResult(false);
        }

        AppDatabase.Write(conn =>
        {
            AppDatabase.Exec(conn, "UPDATE prompt_templates SET is_active = 0 WHERE key = $k", ("$k", key));
            AppDatabase.Exec(conn, "UPDATE prompt_templates SET is_active = 1 WHERE key = $k AND version_id = $v",
                ("$k", key), ("$v", versionId));
        });

        return Task.FromResult(true);
    }

    public string GetBuiltinDefault(string key)
    {
        return key switch
        {
            "persona" => AppSettings.DefaultPersona,
            "agent_prompt" => AppSettings.DefaultAgentPrompt,
            _ => AppSettings.DefaultSystemPrompt
        };
    }

    private static PromptTemplateSnapshot Map(SqliteDataReader r)
    {
        return new PromptTemplateSnapshot
        {
            Id = AppDatabase.Long(r, "id"),
            Key = AppDatabase.Str(r, "key") ?? string.Empty,
            VersionId = AppDatabase.Str(r, "version_id") ?? string.Empty,
            Content = AppDatabase.Str(r, "content") ?? string.Empty,
            Label = AppDatabase.Str(r, "label"),
            IsActive = AppDatabase.Bool(r, "is_active"),
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(AppDatabase.Long(r, "created_unix"))
        };
    }
}
