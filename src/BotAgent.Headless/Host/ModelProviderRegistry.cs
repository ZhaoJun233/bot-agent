using BotAgent.Adapters.Persistence;
using BotAgent.Services.Resilience;

namespace BotAgent.Host;

/// <summary>
/// 模型供应商注册表的运行期视图（#128）。
///
/// 为什么要有这个类：启动装配时把「库里的供应商行」抄成了一份 <c>capturedProviders</c>，
/// 运行期的路由解析（<c>CompositionRoot.TryBuildCurrentProviderRoute</c>）拿它当"当前装配事实"做比对。
/// 那份快照只有启动时更新一次，于是面板改完供应商只会一直报 <c>RestartRequired</c>：
/// 路由解析拒绝所有与快照不一致的供应商 → 整条模型通道进入安全静默（"改了不生效"的另一半）。
///
/// 现在把这份事实收进一个可替换的持有者：保存配置时由
/// <c>CompositionRoot.InspectModelProjection</c> 在同一个提交里重建路由、换掉候选表、再换掉快照。
/// 三条不变量：
/// ① 读侧只看一份快照（<see cref="Captured"/>），不做两次读取混用；
/// ② 先换候选表再换快照 —— 中间窗口只可能"候选表新、快照旧"（路由暂时解析不出），
///    不会反过来（候选表旧、快照新）导致路由指向一个不在候选表里的供应商；
/// ③ 重建失败时保留旧快照（调用方拿不到新状态就不换），绝不落到"没有可用供应商"。
/// </summary>
internal sealed class ModelProviderRegistry
{
    private readonly object _gate = new();
    private volatile IReadOnlyList<PersistedModelProvider> _captured;
    private ProviderFailoverRunner? _runner;

    public ModelProviderRegistry(IReadOnlyList<PersistedModelProvider> captured)
    {
        _captured = captured ?? throw new ArgumentNullException(nameof(captured));
    }

    /// <summary>启动装配阶段设置初始快照（此时故障转移执行器还没挂上，不能走 <see cref="Replace"/>）。</summary>
    public void Initialize(IReadOnlyList<PersistedModelProvider> captured)
    {
        _captured = captured ?? throw new ArgumentNullException(nameof(captured));
    }

    /// <summary>当前"装配事实"：路由解析与回读投影都用它。</summary>
    public IReadOnlyList<PersistedModelProvider> Captured => _captured;

    /// <summary>启动时挂上故障转移执行器；运行期只替换它的候选表，不换实例。</summary>
    public void Attach(ProviderFailoverRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        lock (_gate)
        {
            _runner = runner;
        }
    }

    /// <summary>
    /// 用新的供应商行与候选表替换当前装配事实。抛异常时保持旧状态（调用方据此报错而不是静默降级）。
    /// </summary>
    public void Replace(IReadOnlyList<PersistedModelProvider> captured, IEnumerable<ProviderCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_gate)
        {
            var runner = _runner ?? throw new InvalidOperationException("model_provider_registry_not_attached");
            // 先换候选表：万一后面失败，候选表已新、快照仍旧 —— 路由解析会拒绝而不是指向不存在的供应商。
            runner.ReplaceCandidates(candidates);
            _captured = captured;
        }
    }
}
