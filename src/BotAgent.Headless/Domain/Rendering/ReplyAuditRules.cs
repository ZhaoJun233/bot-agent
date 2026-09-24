using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace BotAgent.Domain.Rendering;

/// <summary>一条出站文本的审计结论（general-agent-platform-plan.md 批次 C）。</summary>
public enum ReplyAuditVerdict
{
    /// <summary>可以发。</summary>
    Allow = 0,

    /// <summary>出现**凭据形状**（key / 私钥 / JWT…）→ 整条不发（两条路都不例外）。</summary>
    BlockCredential = 1,

    /// <summary>出现**本机/服务器路径形状**→ 整条不发（只对聊天那一路上口径；见 <see cref="ReplyAuditRules.Judge" />）。</summary>
    BlockLocalPath = 2,
}

/// <summary>
/// 出站文本的**审计规则**（批次 C，对齐 DSH 的“回复审计”：本机路径/凭据特征 → 整条不发）。
///
/// 为什么放在发送这一层、而且是**整条**拦截：脱敏（显示层）只管名字与号码，管不了“模型把服务器路径写进了群聊回复”；
/// 而一旦发出去就撤不回来了 —— 所以宁可不发（fail-closed），并把**原因码**记进日志与轨迹（正文不进日志）。
///
/// 两条口径（都是刻意的）：
///   · **凭据形状**：两条路都挡，没有“放行”档 —— 密钥进群是不可逆事故；
///   · **路径形状**：只挡**聊天**那一路。`//` 那一路的答案是运维结论，报路径是它的本职（而且只有白名单用户看得到），
///     对它拦路径会把“查看服务器状态”这类正常用法打死。
///
/// 代价说明：路径规则会误伤“群友问 Linux 常识、模型答 /etc/… 之类”的正常回复（见 LocalPathMarkers 的取值），
/// 所以标记表**只收与这套部署相关**的串，不放 `/etc/`、`/usr/` 这类通用路径。
/// </summary>
public static class ReplyAuditRules
{
    /// <summary>凭据形状（**任何一条出站都挡**）。</summary>
    private static readonly string[] CredentialMarkers =
    {
        "sk-", "ghp_", "github_pat_", "xoxb-", "AKIA", "-----BEGIN", "eyJhbGciOi",
        "password=", "secret=", "api_key=", "apikey=", "authorization: bearer",
    };

    /// <summary>
    /// 本机/服务器路径形状（聊天那一路挡）。**只收与这套部署相关的串** ——
    /// 通用系统路径（/etc/、/usr/、C:\\Windows）不在此列，否则“讲个 Linux 常识”都会被拦。
    /// </summary>
    private static readonly string[] LocalPathMarkers =
    {
        "/opt/qqchat", "/data/", "qqchat.db", "docker.sock", "/host/qqchat",
        ".pem", ".env",
    };

    /// <summary>
    /// Windows 盘符路径（<c>C:\…</c> / <c>E:\…</c>）：按**形状**认 ——
    /// 写死具体机器路径既会漏掉别的盘，又会把开发机的目录结构带进仓库。
    /// </summary>
    private static readonly Regex WindowsPath = new(@"[A-Za-z]:\\", RegexOptions.Compiled);

    /// <summary>判定一条出站文本。<paramref name="allowLocalPaths" /> = true 时只挡凭据（`//` 那一路）。</summary>
    public static ReplyAuditVerdict Judge(string? text, bool allowLocalPaths)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ReplyAuditVerdict.Allow;
        }

        if (Hits(text, CredentialMarkers))
        {
            return ReplyAuditVerdict.BlockCredential;
        }

        if (!allowLocalPaths && (Hits(text, LocalPathMarkers) || WindowsPath.IsMatch(text)))
        {
            return ReplyAuditVerdict.BlockLocalPath;
        }

        return ReplyAuditVerdict.Allow;
    }

    /// <summary>原因码（日志与轨迹里只用它，**不用正文**）。</summary>
    public static string Code(ReplyAuditVerdict verdict) => verdict switch
    {
        ReplyAuditVerdict.BlockCredential => "credential_shape",
        ReplyAuditVerdict.BlockLocalPath => "local_path_shape",
        _ => "allowed",
    };

    private static bool Hits(string text, string[] markers)
        => markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
}
