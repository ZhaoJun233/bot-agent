extern alias StorageModule;

using System.Text;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Profiles;
using BotAgent.Services;

namespace BotAgent.Adapters.Persistence;

/// <summary>单个 QQ 用户的档案记录。</summary>
public sealed class MemberProfileRecord
{
    public string Uid { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<MemberMessageRecord> Messages { get; set; } = new();

    /// <summary>各会话范围的长期画像（每个群/私聊一份，不混用）。</summary>
    public List<MemberSummary> Summaries { get; set; } = new();
}

/// <summary>
/// 一个人在某个会话里的长期画像：由模型把历史发言压缩而成。
/// 价值：把“几十条原文”换成“一段画像”，token 降一个数量级，信息密度反而更高。
/// </summary>
public sealed class MemberSummary
{
    /// <summary>会话范围："group:{群号}" 或 "private"。</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>画像正文。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>已折叠到的消息序号：序号不大于它的发言已被写进画像，不再重复注入。</summary>
    public long ThroughSeq { get; set; }

    /// <summary>上次摘要时间（Unix 秒）。</summary>
    public long UpdatedUnix { get; set; }

    /// <summary>已折叠的消息条数（可观测）。</summary>
    public int FoldedCount { get; set; }

    /// <summary>人工设定的覆盖画像文本（优先级高于模型自动摘要）。</summary>
    public string OverrideText { get; set; } = string.Empty;

    /// <summary>画像关联证据链 JSON。</summary>
    public string EvidenceJson { get; set; } = string.Empty;
}

public sealed class MemberMessageRecord
{
    public string Text { get; set; } = string.Empty;

    public long TimeUnix { get; set; }

    public string GroupName { get; set; } = string.Empty;

    /// <summary>来源群号（0 = 私聊）。</summary>
    public long GroupId { get; set; }

    /// <summary>会话内单调序号（对应 ChatMessage.Seq）。旧数据为 0，回退到时间戳比较。</summary>
    public long Seq { get; set; }
}

/// <summary>
/// 人物档案（长期记忆）：SQLite 的 <c>members</c> / <c>member_messages</c> / <c>member_summaries</c> 三张表。
///
/// ⚠ 关键语义（与 JSON 版完全一致）：档案按 **会话（群/私聊）隔离** 读取。
/// 同一个人在 A 群的发言不会被喂进 B 群的上下文（避免串味与隐私泄露）。
///
/// 换成数据库之后的好处：以前一个人一个文件（几百人就是几百个小文件、每次追加都要重写整个 JSON），
/// 现在只插一行；面板查“某人在某群的画像”也是一句 SQL，而不是把全库读进内存再筛。
/// </summary>
public sealed class MemberProfileStore : StorageModule::BotAgent.Adapters.Persistence.MemberProfileStore { }
