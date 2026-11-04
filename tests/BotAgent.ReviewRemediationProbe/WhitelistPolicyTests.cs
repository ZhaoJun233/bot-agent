using BotAgent.Domain.Qq;

namespace BotAgent.ReviewRemediationProbe;

/// <summary>
/// #111：白名单文本的解析口径是纯函数（不碰配置、不碰 IO），最容易在重构里悄悄改掉，
/// 而改掉之后的现象是“名单加了却不生效 / 清了还照收”，只能靠线上反馈发现。
/// 这里把空语义、通配、分隔符、去重、非法 token 全部钉住。
/// </summary>
internal static class WhitelistPolicyTests
{
    internal static Task RunAsync(Action<bool, string> check)
    {
        // 空 = 全部忽略（严格模式）。这不是“没配所以放行”，与官方通道的“空 = 全收”相反。
        var (nullIds, nullAll) = WhitelistPolicy.ParseWhitelist(null);
        check(nullIds.Count == 0 && !nullAll, "null 名单解析成空集且不是通配（严格空 = 全部忽略）");
        var (blankIds, blankAll) = WhitelistPolicy.ParseWhitelist("  \t\r\n ");
        check(blankIds.Count == 0 && !blankAll, "只有空白字符的名单等价于空名单");

        // 通配三种写法。
        check(WhitelistPolicy.ParseWhitelist("*").All, "星号是通配全部");
        check(WhitelistPolicy.ParseWhitelist("all").All, "小写 all 是通配全部");
        check(WhitelistPolicy.ParseWhitelist("ALL").All, "大写 ALL 是通配全部");
        var (mixedIds, mixedAll) = WhitelistPolicy.ParseWhitelist("10001, *");
        check(mixedAll && mixedIds.Contains(10001) && mixedIds.Count == 1, "通配与具体 id 混写时两者都保留（通配优先）");

        // 分隔符：换行、中英文逗号、中英文分号、空格、Tab。
        var (sepIds, _) = WhitelistPolicy.ParseWhitelist("10001\n10002,10003，10004;10005；10006 10007\t10008");
        check(sepIds.Count == 8, $"换行/中英文逗号/中英文分号/空格/Tab 都能作为分隔符（实测 {sepIds.Count} 个）");

        // 去重与非数字丢弃。
        var (dupIds, _) = WhitelistPolicy.ParseWhitelist("10001,10001, 10001");
        check(dupIds.Count == 1 && dupIds.Contains(10001), "重复 id 在解析层去重");
        var (junkIds, junkAll) = WhitelistPolicy.ParseWhitelist("10001,abc,x10002,1e3");
        check(!junkAll && junkIds.Count == 1 && junkIds.Contains(10001), "非数字 token 被丢弃，残留的真实 id 仍生效");

        // 边界数值：负数会被 long.TryParse 接受（当前口径，不在解析层做正数校验）；溢出丢弃。
        var (negativeIds, _) = WhitelistPolicy.ParseWhitelist("-1,10001");
        check(negativeIds.Contains(-1) && negativeIds.Contains(10001), "解析层不校验正数：负整数原样进入集合");
        var (overflowIds, _) = WhitelistPolicy.ParseWhitelist("99999999999999999999");
        check(overflowIds.Count == 0, "超出 long 范围的数字被丢弃");

        // 显示文案与解析同源（日志与面板共用）。
        check(WhitelistPolicy.WhitelistSummary(true, new HashSet<long>()) == "* (全部)", "通配的显示文案是 * (全部)");
        check(WhitelistPolicy.WhitelistSummary(false, new HashSet<long>()) == "(空，忽略全部)", "空名单的显示文案是 (空，忽略全部)");
        check(WhitelistPolicy.WhitelistSummary(false, new HashSet<long> { 10001, 10002 }) == "10001,10002", "具体名单按集合顺序拼成逗号串");

        return Task.CompletedTask;
    }
}
