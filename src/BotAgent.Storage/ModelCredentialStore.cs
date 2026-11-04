using BotAgent.Domain.Configuration;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 已登记模型凭据（provider 独立密钥，引用形式 <c>model:&lt;credentialKey&gt;</c>）的持久化。
///
/// 复用既有的 <c>secrets</c> 表：每条凭据就是一行命名密钥，键名带 <c>model:</c> 前缀，
/// 与面板的固定槽位（apiKey 等）互不覆盖；因此不需要新表、新迁移或新增 SQL 字面量。
/// 读取一律尽力而为：存储异常当作“没有这条凭据”，由上层按凭据不可用拒绝路由。
/// </summary>
public sealed class ModelCredentialStore
{
    internal const string CredentialSecretPrefix = "model:";
    private const int MaximumKeyLength = 128;

    /// <summary>事务内读一条已登记凭据；不存在或不可读返回 null，永不抛出。</summary>
    public static string? LoadInTransaction(Microsoft.Data.Sqlite.SqliteConnection connection, ModelCredentialKey key)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!IsValid(key)) return null;
        try
        {
            var value = SecretsStore.LoadNamedInTransaction(connection, Name(key));
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>事务内写一条已登记凭据；<paramref name="value"/> 为 null/空白表示删除该行。</summary>
    public static void SaveInTransaction(Microsoft.Data.Sqlite.SqliteConnection connection, ModelCredentialKey key,
        string? value, long now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!IsValid(key)) throw new ArgumentException("invalid_model_credential_key");
        SecretsStore.SaveNamedInTransaction(connection, Name(key),
            string.IsNullOrWhiteSpace(value) ? null : value.Trim(), now);
    }

    /// <summary>读一条已登记凭据（自开连接；不存在或不可读返回 null）。</summary>
    public string? Load(ModelCredentialKey key)
    {
        if (!IsValid(key)) return null;
        try
        {
            using var connection = AppDatabase.Open();
            return LoadInTransaction(connection, key);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string Name(ModelCredentialKey key) => CredentialSecretPrefix + key.Value;

    private static bool IsValid(ModelCredentialKey key)
        => !string.IsNullOrWhiteSpace(key.Value) && key.Value.Length <= MaximumKeyLength
            && key.Value.Trim() == key.Value && !key.Value.Contains('\0');
}
