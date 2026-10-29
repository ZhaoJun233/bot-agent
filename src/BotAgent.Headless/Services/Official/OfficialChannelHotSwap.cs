using BotAgent.Services.Qq;

namespace BotAgent.Services.Official;

/// <summary>
/// 官方通道的热切换控制器：按配置变化建立 / 停止官方长连接，并把新实例换进可交换门面。
///
/// 为什么需要它：官方通道是唯一需要**建立 ws 长连接**的上行。它过去只在装配期构造一次，
/// 面板把开关打开后必须重启进程才连 —— 用户看到「开了通道、凭证也对，却始终未连接、
/// 日志一行都没有」，根因就在这儿。
///
/// 三条设计取舍：
///   · 只按「影响连接本身」的字段判断是否需要重建（开关 / AppID / Secret 长度 / 沙箱 / 两个地址）。
///     白名单、冷却、人格这些改了不影响连接，重建反而会把连接来回断掉；
///   · 关闭时不销毁门面，而是换成空壳（InactiveOfficialSource），这样已登记的官方会话
///     不会因为一次保存丢掉目标映射；
///   · 网关本身由 Host 注入工厂构造，本类不引用 Adapters —— 架构探针的 R9 要求
///     Services 层不许依赖 Adapters，而 HttpFetcher / OfficialIdMap 都住在那里。
/// </summary>
public sealed class OfficialChannelHotSwap
{
    private readonly SwappableChannelSource _facade;
    private readonly Action<string> _log;
    private readonly Func<OfficialBotGateway?> _gatewayFactory;
    private readonly object _gate = new();
    private string _signature = string.Empty;

    /// <param name="gatewayFactory">按**当前**配置构造网关；凭证不齐返回 null。</param>
    public OfficialChannelHotSwap(
        SwappableChannelSource facade,
        Action<string> log,
        AppSettings initialSettings,
        Func<OfficialBotGateway?> gatewayFactory)
    {
        _facade = facade;
        _log = log;
        _gatewayFactory = gatewayFactory;

        // 装配期已经按同一份配置建过一次连接，这里把签名预置成「当前态」：
        // 否则启动后的第一次 Sync 会误判为「配置变了」，把刚建好的连接拆掉重建一遍。
        _signature = BuildSignature(initialSettings);
    }

    /// <summary>当前生效的签名（诊断用）。</summary>
    public string Signature => _signature;

    /// <summary>
    /// 按当前配置校准官方通道。签名没变就什么都不做 —— 这条短路是「保存设置不会反复断连」的关键。
    /// </summary>
    public void Sync(AppSettings settings)
    {
        var signature = BuildSignature(settings);

        lock (_gate)
        {
            if (string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                return;
            }

            _signature = signature;
            Rebuild(settings);
        }
    }

    private void Rebuild(AppSettings settings)
    {
        var desired = _gatewayFactory();

        var previous = _facade.Swap(desired ?? (IQqChatSource)new InactiveOfficialSource());

        // 旧实例必须收尾：内部有重连循环 + ws 句柄
        if (previous is OfficialBotGateway oldGateway)
        {
            try
            {
                oldGateway.Stop();
            }
            catch (Exception ex)
            {
                _log($"官方通道旧实例停止失败（忽略）: {ex.Message}");
            }

            oldGateway.Dispose();
        }

        if (desired is null)
        {
            _log("通道层已重建：官方=关（已断开官方长连接，私域通道不受影响）");
            return;
        }

        _log($"通道层已重建：官方=开（appid={settings.OfficialAppId}，" +
             $"{(settings.OfficialSandbox ? "沙箱" : "正式")}环境）");
        desired.Start();
    }

    /// <summary>
    /// 只收会影响连接建立的字段。Secret 用长度参与比对：内容变了长度通常也变，
    /// 而把明文记进签名字符串会让日志/内存里多一份副本。
    /// </summary>
    private static string BuildSignature(AppSettings s)
    {
        var appSecretLength = string.IsNullOrWhiteSpace(s.OfficialAppSecret) ? 0 : s.OfficialAppSecret.Trim().Length;
        return string.Join('|',
            s.OfficialEnabled ? "on" : "off",
            s.OfficialAppId?.Trim() ?? string.Empty,
            appSecretLength.ToString(),
            s.OfficialSandbox ? "sandbox" : "prod",
            s.OfficialApiBase?.Trim() ?? string.Empty,
            s.OfficialTokenUrl?.Trim() ?? string.Empty);
    }
}
