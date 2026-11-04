namespace BotAgent.Domain.Platforms;

/// <summary>
/// 平台标识与账号作用域常量及上下文。
/// </summary>
public static class PlatformId
{
    public const string QqPrivate = "qq.private";
    public const string QqOfficial = "qq.official";
    public const string Local = "local";
    public const string Feishu = "feishu";

    public static string Normalize(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return QqPrivate;
        }

        var p = platform.Trim().ToLowerInvariant();
        return p switch
        {
            "private" or "qq" or "onebot" or "qq.private" => QqPrivate,
            "official" or "qq.official" => QqOfficial,
            "local" => Local,
            "feishu" or "lark" => Feishu,
            _ => p,
        };
    }
}

public static class AccountScope
{
    public const string Legacy = "legacy";
    public const string Default = "default";
}

/// <summary>
/// 平台上下文：标识、账号租户与实例 id。出站调用显式携带，不靠数字号反推。
/// </summary>
public sealed record PlatformContext(
    string PlatformId,
    string AccountScope = AccountScope.Default,
    string InstanceId = "");
