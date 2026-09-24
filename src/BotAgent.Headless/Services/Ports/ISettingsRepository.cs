namespace BotAgent.Services.Ports;

/// <summary>
/// 行为配置的存取端口（由 <c>Adapters/Persistence/SettingsStore</c> 实现，见 §6.4）。
///
/// **为什么不在 `Domain/Ports/`**（对 §6.4 的一处偏差，理由与 §6.3 对 <c>IHttpFetcher</c> 的处理同源）：
/// 它的签名要用 <see cref="AppSettings" />，而配置类型在服务层（`Services/AppSettings.cs`）。
/// 端口可以放在**离它要服务的层最近**的地方，但不能让 Domain 去引用服务层的类型（那会把依赖方向掉个头）。
///
/// 为什么要有它：热更新要把"下一份配置"持久化 —— 它只该说"把这份存下来"，
/// 不该知道配置是整份 JSON 存一行、还是拆成几十个列。
/// </summary>
public interface ISettingsRepository
{
    /// <summary>库文件（配置的真源始终是库，不是文件；这个给启动横幅与面板显示用）。</summary>
    string FilePath { get; }

    /// <summary>库文件在不在盘上（启动横幅如实显示用）。</summary>
    bool ExistsOnDisk { get; }

    /// <summary>库里到底存过配置没有（用来判断"首次部署"：没存过才用环境变量当种子）。</summary>
    bool HasStoredSettings();

    /// <summary>读出配置（读不到/解析失败 = 默认值）。</summary>
    AppSettings Load();

    /// <summary>写入配置（整份覆盖）。</summary>
    void Save(AppSettings settings);
}
