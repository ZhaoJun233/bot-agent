namespace BotAgent.Services;

/// <summary>
/// 配置的**唯一发布点**：持有"当前生效的那一份 AppSettings"，换配置时**整体换引用**，
/// 而不是在共享对象上逐个字段就地改。
///
/// 为什么必须这样（review-findings #4「热更新会改变在途请求」）：
///   面板保存一次会改十几项设置。以前是直接改那个共享实例的字段 ——
///   恰好在那一瞬间开始处理的一轮，会读到"一半新、一半旧"的配置（例如新白名单配旧阈值），
///   而且无法复现、无法确定性测试。
///   现在：在当前版的**副本**上改，改完原子换引用。任何一次读取拿到的都是完整的某一版；
///   正在处理的那一轮继续用它在入口取的那份快照（见 BotAgentHost.GenerateReplyAsync 开头的快照）。
///
/// 三条纪律：
///   ① 读取一律走 <see cref="Current" />（各组件里那个叫 <c>_settings</c> 的只读属性就是它）；
///   ② 写入统一走 <see cref="Apply" /> / <see cref="ApplyPersisted" />（禁止就地改共享配置）；
///   ③ 拿在手里的 <see cref="AppSettings" /> 是**某一版**，别跨轮长期持有它（要长期持有就持这个箱子）。
/// </summary>
public sealed class SettingsBox : BotAgent.Platforms.IPlatformSettingsBox
{
    /// <summary>发布串行化：两个人同时保存时，后一个不能把前一个的修改盖掉（各自都从最新版 fork）。</summary>
    private readonly object _gate = new();

    private AppSettings _current;

    public SettingsBox(AppSettings initial)
        => _current = initial ?? throw new ArgumentNullException(nameof(initial));

    /// <summary>当前生效的那一份（换引用是原子的；每次访问都取最新的）。</summary>
    public AppSettings Current => Volatile.Read(ref _current);

    BotAgent.Platforms.PlatformOptions BotAgent.Platforms.IPlatformSettingsAccessor.Current => Current;

    /// <summary>
    /// 在副本上改 → 原子发布（仅运行时，不落盘）。持久化保存必须走 ApplyPersisted。
    /// Snapshot 同时复制嵌套的平台策略；返回刚发布的版本，不改变在途读取的旧版本。
    /// </summary>
    public AppSettings Apply(Action<AppSettings> mutate)
        => ApplyPersisted(mutate, _ => { });

    /// <summary>
    /// 在同一 writer 边界内串行构造候选、保存、发布并重建派生状态。
    /// 保存失败不发布、不执行 published；mutate 只应改候选，副作用放 published。
    /// published 是提交后的操作：若它失败，异常向上传播，但已提交/发布的版本不回滚。
    /// </summary>
    public AppSettings ApplyPersisted(Action<AppSettings> mutate, Action<AppSettings> persist,
        Action<AppSettings>? published = null)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(persist);

        lock (_gate)
        {
            var next = Current.Snapshot();
            mutate(next);
            persist(next);
            // ⚠ Interlocked.Exchange 返回的是**换出去的那个旧值**，不是刚进去的新值 ——
            // 这里必须显式返回 next，否则调用方（落盘 / 重建派生物）会拿着旧的一份去用
            // （2026-09-22 踩过：内存里是新配置、库里写回旧配置，面板"改了不回滚"的用例直接红）。
            Interlocked.Exchange(ref _current, next);
            published?.Invoke(next);
            return next;
        }
    }
}
