using System.Collections.Generic;
using System.Linq;
using System.Text;
using BotAgent.Domain.Permissions;

namespace BotAgent.Domain.Tools;

/// <summary>
/// 把工具目录渲染成**给模型看的一小段提示词**（general-agent-platform-plan.md 批次 D）。
///
/// 为什么要有它：聊天那路的动作清单以前是**分散**写在提示词各处（联网搜索一段、语音一段、表情包一段…），
/// 模型看得到“怎么做”，却看不到“**有哪些工具、参数长什么样、哪个需要批准**”。
/// 现在由目录生成一份**短的**清单（只列本次策略允许的那些），两条纪律：
///   · **按策略裁剪**（§10.2 坑 2：提示词膨胀 —— 只列这次真能用的，参数说明一律截短）；
///   · **口径唯一**：名字与说明取自 ToolSpec，不在这里再写一遍（否则迟早两处打架）。
/// </summary>
public static class ToolPromptText
{
    /// <summary>段落标题（ArchitectureProbe 按这个锚点钉“清单真的进了提示词”）。</summary>
    public const string Header = "[可用工具]";

    /// <summary>参数说明的截断长度（提示词就是成本与延迟，所以宁可少说几句）。</summary>
    public const int MaxParamChars = 28;

    /// <summary>聊天那一路：只列这次策略允许的工具（其余写了也不会执行）。</summary>
    public static string Render(ChatCapabilitySet capabilities)
        => Render(ChatToolSpecs.All, capabilities.Policy);

    /// <summary>通用渲染（policy 决定“哪些露出来、哪些要批准”）。</summary>
    public static string Render(IReadOnlyList<ToolSpec> specs, ToolPolicy policy)
    {
        // 只列**可选能力**（PromptVisible）且这次放行的：默认行为（chat.reply）不进清单。
        var allowed = specs.Where(s => s.PromptVisible && policy.AllowedTools.Contains(s.Id)).ToList();
        if (allowed.Count == 0)
        {
            // 一个都没开：这一段**整段不出现**（与“开关全关时提示词逐字不变”的纪律一致）。
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("\n\n").Append(Header).Append('\n');
        sb.Append("（只列这次开着的；没列的写了也不会执行。）\n");
        foreach (var spec in allowed)
        {
            sb.Append("• ").Append(spec.Id).Append('：').Append(spec.Summary);
            var param = Compact(spec.Parameters);
            if (param.Length > 0)
            {
                sb.Append(" —— ").Append(param);
            }

            if (policy.RequiresApproval(spec))
            {
                sb.Append("〔需批准〕");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>参数说明压成一行（换行会破坏清单的可读性；超长截断）。</summary>
    private static string Compact(string? text)
    {
        var one = (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (one.Contains("  ", StringComparison.Ordinal))
        {
            one = one.Replace("  ", " ", StringComparison.Ordinal);
        }

        return one.Length <= MaxParamChars ? one : one[..MaxParamChars] + "…";
    }
}
