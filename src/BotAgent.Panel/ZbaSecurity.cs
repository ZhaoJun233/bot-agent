using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BotAgent.Panel;

internal sealed record ZbaAuthOutcome(
    ZbaPrincipal? Principal,
    int Status,
    string? Code,
    string? Message)
{
    internal bool Ok => Principal is not null;

    internal static ZbaAuthOutcome Allow(ZbaPrincipal principal) => new(principal, 0, null, null);

    internal static ZbaAuthOutcome Deny(int status, string code, string message) => new(null, status, code, message);
}

/// <summary>
/// ZBA 只用 Bearer 凭据，不使用 Cookie；因此写路径没有浏览器 CSRF 面。
/// 无凭据时默认全拒绝（fail-closed），仅显式打开 <see cref="ZbaOptions.AllowLoopbackWithoutToken" />
/// 的本机开发场景才把回环视为管理员。
/// </summary>
internal sealed class ZbaSecurity
{
    private const int FailureLimit = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);

    private readonly ZbaOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _failures = new(StringComparer.Ordinal);
    private readonly byte[]? _adminHash;
    private readonly byte[]? _readHash;

    internal ZbaSecurity(ZbaOptions options, Func<DateTimeOffset> now)
    {
        _options = options;
        _now = now;
        _adminHash = Hash(options.AdminToken);
        _readHash = Hash(options.ReadToken);
    }

    internal bool Configured => _adminHash is not null || _readHash is not null;

    internal ZbaAuthOutcome Authorize(ZbaRequest request, ZbaScope required)
    {
        if (!Configured)
        {
            if (_options.AllowLoopbackWithoutToken && IsLoopback(request.RemoteAddress))
                return ZbaAuthOutcome.Allow(new ZbaPrincipal(ZbaScope.Admin, "loopback", true));
            return ZbaAuthOutcome.Deny(503, "zba_not_configured",
                "ZBA 未配置访问凭据；设置 BOTAGENT_ZBA_TOKEN 后才开放。");
        }

        var remote = request.RemoteAddress ?? "unknown";
        if (IsBlocked(remote))
            return ZbaAuthOutcome.Deny(429, "too_many_authentication_failures", "认证失败次数过多，请稍后重试。");

        if (!request.Headers.TryGetValue("Authorization", out var header) || string.IsNullOrWhiteSpace(header))
        {
            RecordFailure(remote);
            return ZbaAuthOutcome.Deny(401, "unauthorized", "缺少 Bearer 凭据。");
        }

        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            RecordFailure(remote);
            return ZbaAuthOutcome.Deny(401, "unauthorized", "Authorization 必须是 Bearer 凭据。");
        }

        var tokenHash = Hash(parts[1]);
        if (tokenHash is null)
        {
            RecordFailure(remote);
            return ZbaAuthOutcome.Deny(401, "unauthorized", "凭据无效。");
        }

        var isAdmin = Matches(tokenHash, _adminHash);
        var isRead = Matches(tokenHash, _readHash);
        if (!isAdmin && !isRead)
        {
            RecordFailure(remote);
            return ZbaAuthOutcome.Deny(401, "unauthorized", "凭据无效。");
        }

        if (required == ZbaScope.Admin && !isAdmin)
            return ZbaAuthOutcome.Deny(403, "forbidden", "该操作需要管理员凭据。");

        ClearFailures(remote);
        var subject = isAdmin ? "admin" : "reader";
        var scope = isAdmin ? ZbaScope.Admin : ZbaScope.Read;
        return ZbaAuthOutcome.Allow(new ZbaPrincipal(scope, subject, IsLoopback(request.RemoteAddress)));
    }

    private bool IsBlocked(string remote)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(remote, out var entry)) return false;
            if (_now() - entry.Start >= FailureWindow)
            {
                _failures.Remove(remote);
                return false;
            }

            return entry.Count >= FailureLimit;
        }
    }

    private void RecordFailure(string remote)
    {
        lock (_gate)
        {
            var now = _now();
            if (_failures.Count > 1024) Prune(now);
            if (!_failures.TryGetValue(remote, out var entry) || now - entry.Start >= FailureWindow)
                _failures[remote] = (now, 1);
            else
                _failures[remote] = (entry.Start, entry.Count + 1);
        }
    }

    private void ClearFailures(string remote)
    {
        lock (_gate) _failures.Remove(remote);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _failures.Where(pair => now - pair.Value.Start >= FailureWindow).Select(pair => pair.Key).ToList())
            _failures.Remove(key);
    }

    private static bool Matches(byte[]? presented, byte[]? expected)
        => presented is not null && expected is not null && CryptographicOperations.FixedTimeEquals(presented, expected);

    private static byte[]? Hash(string? value)
        => string.IsNullOrEmpty(value) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(value));

    internal static bool IsLoopback(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        if (!IPAddress.TryParse(address, out var parsed)) return false;
        if (IPAddress.IsLoopback(parsed)) return true;
        return parsed.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(parsed.MapToIPv4());
    }
}
