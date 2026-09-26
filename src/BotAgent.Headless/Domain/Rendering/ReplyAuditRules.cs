using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace BotAgent.Domain.Rendering;

/// <summary>一条出站文本的审计结论（确定性 DLP 守卫）。</summary>
public enum ReplyAuditVerdict
{
    Allow = 0,
    BlockCredential = 1,
    BlockLocalPath = 2,
    BlockPrivateNetwork = 3,
    BlockPhoneNumber = 4,
    BlockSystemPrompt = 5,
}

/// <summary>
/// 出站文本的确定性 DLP 规则。
/// 凭据、内网地址、手机号和系统提示词特征在两条发送路径上都拦截；
/// 本机路径保留原有口径，只在聊天回复路径拦截。
/// </summary>
public static class ReplyAuditRules
{
    private static readonly string[] CredentialMarkers =
    {
        "sk-", "ghp_", "github_pat_", "xoxb-", "AKIA", "-----BEGIN", "eyJhbGciOi",
        "password=", "secret=", "api_key=", "apikey=", "authorization: bearer",
    };

    private static readonly string[] LocalPathMarkers =
    {
        "/opt/qqchat", "/data/", "qqchat.db", "docker.sock", "/host/qqchat",
        ".pem", ".env",
    };

    private static readonly string[] SystemPromptMarkers =
    {
        "[当前时间]", "[机器人人设档案]", "[会话参与者档案", "[可选的结构化动作]",
        "ignore previous instructions", "system message:", "<|system|>",
    };

    private static readonly Regex WindowsPath = new(@"[A-Za-z]:\\", RegexOptions.Compiled);
    private static readonly Regex PrivateNetworkAddress = new(
        @"(?<![\d.])(?:10(?:\.\d{1,3}){3}|127(?:\.\d{1,3}){3}|192\.168(?:\.\d{1,3}){2}|172\.(?:1[6-9]|2\d|3[0-1])(?:\.\d{1,3}){2})(?![\d.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PhoneNumber = new(
        @"(?<!\d)(?:\+?86[ -]?)?1[3-9]\d{9}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>判定一条出站文本；返回原因码所需的结构化结论，不返回命中的正文。</summary>
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

        if (PrivateNetworkAddress.IsMatch(text))
        {
            return ReplyAuditVerdict.BlockPrivateNetwork;
        }

        if (PhoneNumber.IsMatch(text))
        {
            return ReplyAuditVerdict.BlockPhoneNumber;
        }

        if (Hits(text, SystemPromptMarkers))
        {
            return ReplyAuditVerdict.BlockSystemPrompt;
        }

        if (!allowLocalPaths && (Hits(text, LocalPathMarkers) || WindowsPath.IsMatch(text)))
        {
            return ReplyAuditVerdict.BlockLocalPath;
        }

        return ReplyAuditVerdict.Allow;
    }

    /// <summary>原因码（日志与轨迹里只用它，不记录正文）。</summary>
    public static string Code(ReplyAuditVerdict verdict) => verdict switch
    {
        ReplyAuditVerdict.BlockCredential => "credential_shape",
        ReplyAuditVerdict.BlockLocalPath => "local_path_shape",
        ReplyAuditVerdict.BlockPrivateNetwork => "private_network_address",
        ReplyAuditVerdict.BlockPhoneNumber => "phone_number",
        ReplyAuditVerdict.BlockSystemPrompt => "system_prompt_fingerprint",
        _ => "allowed",
    };

    private static bool Hits(string text, string[] markers)
        => markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
}
