using System.Text.RegularExpressions;

namespace QQChatAgent.Services.Agent;

/// <summary>
/// 脱敏：把“群名 / 昵称 / QQ 号”这类能指认到人的东西遮掉（号主 2026-09-17 要的开关）。
///
/// 用在哪：**列出会话**这类场景 —— 群里的 <c>//sessions</c> / <c>//sessions all</c> / <c>//runs</c> / <c>//pi</c>
/// 回复、面板的会话列表与总览、面板里显示聊天名的地方。
/// 用在哪都行：只做“显示层”的遮盖，**存储与 API 的 key 保持原样**（不然面板点不动、命令也认不出来）。
///
/// 规则（刻意做得能认出来、但认不出是谁）：
///   • 长数字（QQ 号/群号，≥6 位）→ 保留前 3 与后 2，中间 <c>***</c>：`123456789` → `123***89`
///   • 聊天名 → 不显示真名，换成 `群聊 123***89` / `好友 456***62`（从会话 key 推）
///   • 文本里的**已知昵称** → `群友A`/`群友B`…（同一个名字在同一次输出里映射一致）
///   • 文本里其它长数字（可能夹在提示词/结果里）→ 同上遮盖
/// </summary>
public static class AgentMask
{
    private static readonly Regex LongDigits = new(@"(?<!\d)\d{6,}(?!\d)", RegexOptions.Compiled);

    /// <summary>遮盖一段文本里的长数字（QQ 号、群号…）。</summary>
    public static string Digits(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : LongDigits.Replace(text, m => Shorten(m.Value));

    /// <summary>遮盖长数字 + 把已知昵称换成 群友A/B/…（<paramref name="knownNames"/> 里没出现过的名字不动）。</summary>
    public static string Text(string? text, IEnumerable<string>? knownNames = null)
    {
        var result = Digits(text);
        if (string.IsNullOrEmpty(result) || knownNames is null)
        {
            return result ?? string.Empty;
        }

        var index = 0;
        foreach (var name in knownNames
                     .Where(n => !string.IsNullOrWhiteSpace(n) && n.Length >= 2)
                     .Distinct()
                     .OrderByDescending(n => n.Length))   // 长的先替换，避免短名把长名切碎
        {
            if (!result.Contains(name, StringComparison.Ordinal))
            {
                continue;
            }

            var alias = $"群友{(char)('A' + index % 26)}";
            result = result.Replace(name, alias, StringComparison.Ordinal);
            index++;
        }

        return result;
    }

    /// <summary>聊天的显示名：不露真名，用 key 里的 id 遮盖后当名字。</summary>
    /// <remarks>
    /// 通道前缀要先吃掉：官方通道的 key 是 <c>official:group:8000…</c>（三段），
    /// 直接按第一个冒号切会把通道名当 id 显出来，标签也会错标成好友。
    /// </remarks>
    public static string ChatLabel(string sourceKey, string? realName = null)
    {
        var key = Services.Qq.Channels.Strip(sourceKey);
        var (isGroup, parsedId) = Services.Qq.Channels.Parse(sourceKey);
        var id = parsedId > 0
            ? parsedId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : (key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key);
        var prefix = isGroup ? "群聊" : "好友";
        var tag = Services.Qq.Channels.IsOfficial(Services.Qq.Channels.ChannelOf(sourceKey)) ? "官方" : string.Empty;
        return $"{tag}{prefix} {Shorten(id)}";
    }

    /// <summary>只留前 3 后 2：123456789 → 123***89（太短的整段星星）。</summary>
    public static string Shorten(string? id)
    {
        var text = (id ?? string.Empty).Trim();
        if (text.Length <= 5)
        {
            return new string('*', Math.Max(3, text.Length));
        }

        return text.Length <= 9
            ? $"{text[..3]}***{text[^2..]}"
            : $"{text[..3]}***{text[^2..]}";
    }
}
