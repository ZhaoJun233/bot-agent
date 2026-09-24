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
///   ② 写入只有 <see cref="Apply" /> 一条路（<c>_settings.X = …</c> 这种就地赋值不许再出现）；
///   ③ 拿在手里的 <see cref="AppSettings" /> 是**某一版**，别跨轮长期持有它（要长期持有就持这个箱子）。
/// </summary>
public sealed class SettingsBox
{
    /// <summary>发布串行化：两个人同时保存时，后一个不能把前一个的修改盖掉（各自都从最新版 fork）。</summary>
    private readonly object _gate = new();

    private AppSettings _current;

    public SettingsBox(AppSettings initial)
        => _current = initial ?? throw new ArgumentNullException(nameof(initial));

    /// <summary>当前生效的那一份（换引用是原子的；每次访问都取最新的）。</summary>
    public AppSettings Current => Volatile.Read(ref _current);

    /// <summary>
    /// 在**副本**上改 → 原子发布。返回刚发布的那一份，调用方继续用它重建派生物 / 落盘。
    /// 副本是浅拷贝（<see cref="AppSettings.Snapshot" />）：这个类型只有标量字段，够用。
    /// </summary>
    public AppSettings Apply(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var next = Current.Snapshot();
            mutate(next);
            // ⚠ Interlocked.Exchange 返回的是**换出去的那个旧值**，不是刚进去的新值 ——
            // 这里必须显式返回 next，否则调用方（落盘 / 重建派生物）会拿着旧的一份去用
            // （2026-09-22 踩过：内存里是新配置、库里写回旧配置，面板"改了不回滚"的用例直接红）。
            Interlocked.Exchange(ref _current, next);
            return next;
        }
    }
}
