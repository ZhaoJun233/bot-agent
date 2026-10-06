using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// 场景预设（V3 §9.3）：**用配置差异表达，不写成隐藏业务分支**。
/// 名字不认识 → 最低权限策略（Fail-Closed），绝不“默认全开”。
/// </summary>
public static class ScenarioPresets
{
    public const string OnDemand = "on-demand";
    public const string Research = "research";
    public const string Social = "social";

    public static readonly string[] All = { OnDemand, Research, Social };

    /// <summary>
    /// 按预设名给出**建议**策略（只影响能力白名单与预算；不代表已验证的最优参数）。
    /// 预设里不出现密钥、管理员名单、文件路径、命令、桥地址或任意 URL。
    /// </summary>
    public static ToolPolicy Resolve(string? name, int policyVersion = 1)
    {
        var token = (name ?? string.Empty).Trim().ToLowerInvariant();
        return token switch
        {
            // 按需参与：默认只在明确提及时回复，能力关到最小
            OnDemand => new ToolPolicy(
                new HashSet<string>(StringComparer.Ordinal) { "chat.reply" },
                MaxCallsPerRun: 1,
                ApprovableCategories: new HashSet<ToolCategory>(),
                PolicyVersion: policyVersion),

            // 资料讨论：允许受限的搜索/网页读取（单轮预算 2 次）
            Research => new ToolPolicy(
                new HashSet<string>(StringComparer.Ordinal) { "chat.reply", "web.search", "web.read" },
                MaxCallsPerRun: 2,
                ApprovableCategories: new HashSet<ToolCategory> { ToolCategory.WebRead },
                PolicyVersion: policyVersion),

            // 多人交流：允许有限度参与（额外动作每轮 3 次）
            Social => new ToolPolicy(
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "chat.reply", "voice.speak", "sticker.send", "poke.send", "music.listen", "music.share",
                },
                MaxCallsPerRun: 3,
                ApprovableCategories: new HashSet<ToolCategory> { ToolCategory.SendMessage },
                PolicyVersion: policyVersion),

            _ => ToolPolicy.DenyAll(policyVersion),
        };
    }
}
