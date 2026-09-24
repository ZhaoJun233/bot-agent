using BotAgent.Domain.Permissions;

namespace BotAgent.Domain.Tools;

/// <summary>
/// 聊天这一路（普通群聊回复）的工具声明 —— 原来内联写在 <c>ChatCapabilitySet.DefaultRegistry</c> 里，
/// 批次 A 起换成 <see cref="ToolSpec" />：同一批对象既是登记表条目，也是统一目录的一行
/// （避免“登记表一份、目录又一份”）。
///
/// 两条纪律：
///   · **Id 与今天逐字一致**（<c>chat.reply</c> / <c>web.search</c> / …）—— 它们出现在日志的
///     <c>[能力]</c> 行与审批单里，改字就是改对外契约；
///   · <see cref="ToolDescriptor.Summary" /> / <see cref="ToolSpec.Parameters" /> 是**给模型看的那一份**
///     （批次 D 会把它们拼进提示词）—— 与提示词里别处的描述只能有一处真源。
/// </summary>
public static class ChatToolSpecs
{
    /// <summary>聊天那路的动作执行者（<c>Services/Reply/ReplyPipeline.cs</c> 里的 if/else）。</summary>
    public const string ExecutorActions = "chat.actions";

    /// <summary>审批/提问这条链的执行者（<c>Services/Permissions/ApprovalUseCase.cs</c>）。</summary>
    public const string ExecutorApproval = "chat.approval";

    /// <summary>全部 10 条（顺序 = 面板与提示词里的顺序；与改造前一致）。</summary>
    public static readonly IReadOnlyList<ToolSpec> All = new ToolSpec[]
    {
        new("chat.reply", ToolCategory.ConversationRead, "回复当前会话",
            ReadOnly: false, ExecutorActions, "reply（要说的话；留空 = 不发）", ToolDefaultPolicy.FollowSwitch,
            PromptVisible: false),
        new("web.search", ToolCategory.WebRead, "联网搜索",
            ReadOnly: true, ExecutorActions, "search（要联网查的问题，2~120 字）", ToolDefaultPolicy.FollowSwitch),
        new("web.read", ToolCategory.WebRead, "读网页正文",
            ReadOnly: true, ExecutorActions, "read（要读的网页 URL）", ToolDefaultPolicy.FollowSwitch),
        new("music.listen", ToolCategory.WebRead, "去听一首歌（只读外部数据）",
            ReadOnly: true, ExecutorActions, "listen（歌名 / 歌手，2~60 字）", ToolDefaultPolicy.FollowSwitch),
        new("music.share", ToolCategory.SendMessage, "分享歌曲卡片",
            ReadOnly: false, ExecutorActions, "shareSong（要分享的歌名，2~60 字）", ToolDefaultPolicy.FollowSwitch),
        new("voice.speak", ToolCategory.SendMessage, "用语音说一句",
            ReadOnly: false, ExecutorActions, "speak（要用语音说的那句；true = 就说 reply）", ToolDefaultPolicy.FollowSwitch),
        new("sticker.send", ToolCategory.SendMessage, "发表情包",
            ReadOnly: false, ExecutorActions, "sticker（从候选里挑一个 id；不写就不发）", ToolDefaultPolicy.FollowSwitch),
        new("poke.send", ToolCategory.SendMessage, "戳一戳",
            ReadOnly: false, ExecutorActions, "poke（戳谁：写目标 QQ 号 / sender / me）", ToolDefaultPolicy.FollowSwitch),
        new(ApprovalFlow.FixedToolId, ToolCategory.SendMessage, ApprovalFlow.FixedToolSummary,
            ReadOnly: false, ExecutorApproval, "toolRequest（服务端只认这一个固定假工具）", ToolDefaultPolicy.FollowSwitch),
        new(ApprovalFlow.QuestionToolId, ToolCategory.SendMessage, "往当前会话问一个问题（带编号与有效期；不授予任何权限）",
            ReadOnly: false, ExecutorApproval, "reply（要问的话；服务端包编号与有效期）", ToolDefaultPolicy.FollowSwitch),
    };

    /// <summary>登记表视图（唯一真相：登记表与目录共用同一批对象，不是两份清单）。</summary>
    public static ToolRegistry DefaultRegistry { get; } = BuildRegistry();

    private static ToolRegistry BuildRegistry()
    {
        var registry = new ToolRegistry();
        foreach (var spec in All)
        {
            registry.Register(spec);
        }

        return registry;
    }
}
