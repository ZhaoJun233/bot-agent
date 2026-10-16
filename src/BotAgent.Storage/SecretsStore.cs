using BotAgent.Services;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Configuration;
using Microsoft.Data.Sqlite;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 面板里填的密钥（模型 API Key、网易云登录态），存在 SQLite 的 <c>secrets</c> 表里。
///
/// 为什么单独一张表而不是塞进 settings：行为配置那份是要给人看、给面板读的（也方便贴出来排障），
/// 密钥混进去一不小心就跟着泄露。表分开之后：面板/日志/接口响应永远只给掩码（<c>sk-****</c>），
/// 要分享配置只需要导出 settings 那一行。库文件权限设成 600（见 <see cref="RestrictDbPermissions"/>）。
/// 环境变量仍然优先于“从未在面板里填过”的默认值：面板填过之后以面板为准。
///
/// 网易云登录态为什么也要存这里：以前它只活在自建 API 容器的进程内存里，
/// **容器一重建（升级镜像 / compose up 重创）就得重新扫码** —— 管理员反馈“老是掉登录”就是这个。
/// 现在扫码成功后把 cookie 存进本表，并随手带上每个请求（见 NeteaseMusicClient），
/// 与那个容器活着不活着无关。
/// </summary>
public class SecretsStore : ISecretsRepository
{
    private const string ReadSecretSql = "SELECT value FROM secrets WHERE name=$name";
    private const string DeleteSecretSql = "DELETE FROM secrets WHERE name=$name";
    private const string SaveSecretSql = "INSERT INTO secrets(name,value,updated_unix) VALUES($name,$value,$now) " +
        "ON CONFLICT(name) DO UPDATE SET value=excluded.value,updated_unix=excluded.updated_unix";
    internal static string SlotName(WritableSecretSlot slot) => slot switch
    {
        WritableSecretSlot.ModelApiKey => "apiKey",
        WritableSecretSlot.AgentServerKey => "agentServerKey",
        WritableSecretSlot.TtsApiKey => "ttsKey",
        WritableSecretSlot.FeishuAppSecret => "feishuAppSecret",
        WritableSecretSlot.FeishuEncryptKey => "feishuEncryptKey",
        WritableSecretSlot.FeishuVerificationToken => "feishuVerificationToken",
        _ => throw new ArgumentException("invalid_secret_slot")
    };

    internal static string? LoadInTransaction(SqliteConnection connection, WritableSecretSlot slot)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ReadSecretSql;
        command.Parameters.AddWithValue("$name", SlotName(slot));
        return command.ExecuteScalar() as string;
    }

    internal static void SaveInTransaction(SqliteConnection connection, WritableSecretSlot slot,
        string? normalizedValue, long nowUnixSeconds)
    {
        if (normalizedValue is not null && string.IsNullOrWhiteSpace(normalizedValue))
            throw new ArgumentException("invalid_secret_replace");
        SaveNamedInTransaction(connection, SlotName(slot), normalizedValue, nowUnixSeconds);
        if (slot == WritableSecretSlot.FeishuVerificationToken)
            AppDatabase.SetMetaInTransaction(connection, "verification_token_initialized",
                normalizedValue is null ? "deleted" : "initialized");
    }

    private static void SaveNamedInTransaction(SqliteConnection connection, string name, string? value, long now)
    {
        if (value is null) AppDatabase.Exec(connection, DeleteSecretSql, ("$name", name));
        else AppDatabase.Exec(connection, SaveSecretSql, ("$name", name), ("$value", value), ("$now", now));
    }
    /// <summary>数据根目录（与 data/、stickers/ 同级）。</summary>
    public static void Init(string dataRoot)
    {
        // 库文件是进程级单例，这里只负责“把权限收紧”（库文件里现在装着密钥）。
        RestrictDbPermissions();
    }

    /// <summary>读取面板里填过的 API Key；没有/读失败返回 null（调用方回退环境变量）。</summary>
    public string? LoadApiKey() => Load("apiKey");

    /// <summary>写入（空值 = 删掉该条）。返回是否成功。</summary>
    public bool SaveApiKey(string? apiKey) => Save("apiKey", apiKey);

    /// <summary>读取面板里填过的服务器 agent 专用密钥；没有/读失败返回 null（调用方回退环境变量）。</summary>
    public string? LoadAgentServerKey() => Load("agentServerKey");

    /// <summary>保存服务器 agent 专用密钥（空值 = 清掉，回退环境变量）。</summary>
    public bool SaveAgentServerKey(string? apiKey) => Save("agentServerKey", apiKey);

    /// <summary>网易云登录态（扫码成功后由面板那条链路存下来）；没有就回退环境变量。</summary>
    public string? LoadNeteaseCookie() => Load("neteaseCookie");
    /// <summary>保存网易云登录态（空值 = 清掉，下次回退环境变量）。</summary>
    public bool SaveNeteaseCookie(string? cookie) => Save("neteaseCookie", cookie);

    /// <summary>
    /// 云端 TTS（语音合成）的厂商密钥。
    /// 与其它密钥同规矩：只存密钥库、面板只回显掩码、不写 settings.json。
    /// 但多一步：机器人拿到后还要**写给 tts 容器**（它是另一个进程，读不到我们的库）——
    /// 落盘成文件让容器挂载读取，见 <c>WriteTtsConf</c>。
    /// </summary>
    public string? LoadTtsKey() => Load("ttsKey");

    public bool SaveTtsKey(string? key) => Save("ttsKey", key);

    /// <summary>
    /// Historical compatibility member: official credentials are not a database source.
    /// The Host reads only <c>QQCHAT_OFFICIAL_APP_SECRET</c>; old rows remain untouched.
    /// </summary>
    public string? LoadOfficialSecret() => Load("officialAppSecret");

    /// <summary>Always rejects; official credentials cannot be persisted through this repository.</summary>
    public bool SaveOfficialSecret(string? secret) => Save("officialAppSecret", secret);

    /// <summary>飞书通道（Feishu Bot API）的 AppSecret 与 EncryptKey。</summary>
    public string? LoadFeishuSecret() => Load("feishuAppSecret");

    public bool SaveFeishuSecret(string? secret) => Save("feishuAppSecret", secret);

    public string? LoadFeishuEncryptKey() => Load("feishuEncryptKey");

    public bool SaveFeishuEncryptKey(string? key) => Save("feishuEncryptKey", key);

    /// <summary>读一条密钥（没有/读失败返回 null）。</summary>
    public string? Load(string name) => Load(name, throwOnError: false);

    public string? Load(string name, bool throwOnError)
    {
        // Historical rows remain untouched, but official credentials are never a database source.
        if (string.Equals(name, "officialAppSecret", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var value = AppDatabase.Scalar<string>(ReadSecretSql, ("$name", name));
            if (throwOnError && value is not null && string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("Invalid stored secret: " + name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex)
        {
            if (throwOnError) throw;
            FileLog.Warn("Secrets", $"密钥读取失败（回退环境变量）：{ex.Message}");
            return null;
        }
    }

    /// <summary>写一条密钥（空值 = 删掉该条）。返回是否成功。</summary>
    public bool Save(string name, string? value) => Save(name, value, throwOnError: false);

    public bool Save(string name, string? value, bool throwOnError)
    {
        try
        {
            if (string.Equals(name, "officialAppSecret", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("official_secret_environment_only");
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            AppDatabase.Write(conn =>
            {
                if (ConfigurationStore.RequiresCoordinatedConfigurationWrites && name is
                    "apiKey" or "agentServerKey" or "ttsKey" or "feishuAppSecret" or "feishuEncryptKey" or "feishuVerificationToken")
                    throw new InvalidOperationException("configuration_writer_required");
                SaveNamedInTransaction(conn, name, normalized, Clock.Now.ToUnixTimeSeconds());
            });

            RestrictDbPermissions();
            return true;
        }
        catch (Exception ex)
        {
            if (throwOnError) throw;
            FileLog.Warn("Secrets", $"密钥写入失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>只给当前用户读写（Windows 上跳过：部署目录本来就只有管理员能看）。</summary>
    private static void RestrictDbPermissions()
    {
        if (OperatingSystem.IsWindows() || !AppDatabase.IsReady)
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(AppDatabase.FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            FileLog.Warn("Secrets", $"库文件权限设置失败（仍可用，但请手工 chmod 600）：{ex.Message}");
        }
    }
}
