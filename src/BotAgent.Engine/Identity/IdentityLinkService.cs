using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using BotAgent.Domain.Identity;

namespace BotAgent.Engine.Identity;

/// <summary>
/// ADR-0006 可选跨平台本人身份关联服务（默认关闭）：
/// - 120 秒单次内存挑战码 + 双向可靠私聊确认，用后即毁，禁止同端自绑或跨人重放；
/// - 单一端点全库唯一绑定，解绑即刻撤销共享读写凭据；
/// - 绝不暴露群角色、审批权或聊天记录共享。
/// </summary>
public sealed class IdentityLinkService : IIdentityLinkResolver
{
    public const int ChallengeTtlSeconds = 120;
    private const int MaxPendingChallenges = 32;
    private const int MaxFailedAttemptsPerWindow = 5;
    private const int MaxLinks = 32;

    private readonly IIdentityLinkStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string, string> _maskUser;
    private readonly byte[] _hmacSecret = RandomNumberGenerator.GetBytes(32);
    private readonly object _gate = new();
    private readonly List<PendingChallenge> _challenges = new();
    private readonly Dictionary<string, (int Failures, long WindowStartUnix)> _failedAttempts = new(StringComparer.Ordinal);

    private sealed record PendingChallenge(
        string ChallengeCode,
        IdentityEndpointKey Source,
        long ExpiresAtUnix);

    public IdentityLinkService(
        IIdentityLinkStore store,
        Func<DateTimeOffset>? utcNow = null,
        Func<string, string>? maskUser = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNow = utcNow ?? (Func<DateTimeOffset>)(() => BotAgent.Engine.Runtime.EngineClock.UtcNow);
        _maskUser = maskUser ?? DefaultMask;
    }

    public bool IsEnabled
    {
        get { lock (_gate) return _store.Load().Enabled; }
    }

    public IdentityLinkStatusView GetStatus()
    {
        lock (_gate)
        {
            var state = _store.Load();
            var masked = state.Links
                .Select(l => new IdentityEndpointMask(
                    l.CanonicalPersonId,
                    l.Endpoint.PlatformId,
                    l.Endpoint.AccountScope,
                    l.Endpoint.InstanceId,
                    _maskUser(l.Endpoint.NativeUserId),
                    l.LinkedUnix))
                .ToArray();
            return new IdentityLinkStatusView(state.Enabled, state.Preferences, masked, state.WorkspaceCanonicalPersonId);
        }
    }

    public IdentityOperationResult SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            var state = _store.Load();
            _store.Save(state with { Enabled = enabled });
            if (!enabled)
            {
                _challenges.Clear();
                _failedAttempts.Clear();
            }
            return new(true, enabled ? "identity_linking_enabled" : "identity_linking_disabled");
        }
    }

    public IdentityOperationResult UpdateSharePreferences(IdentitySharePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            var state = _store.Load();
            _store.Save(state with { Preferences = preferences });
            return new(true, "preferences_updated");
        }
    }

    public IdentityOperationResult BindWorkspace(string? canonicalPersonId)
    {
        lock (_gate)
        {
            var state = _store.Load();
            if (string.IsNullOrWhiteSpace(canonicalPersonId))
            {
                _store.Save(state with { WorkspaceCanonicalPersonId = null });
                return new(true, "workspace_unbound");
            }
            if (!state.Enabled)
                return new(false, "identity_linking_disabled");
            var trimmed = canonicalPersonId.Trim();
            var count = state.Links.Count(l => string.Equals(l.CanonicalPersonId, trimmed, StringComparison.Ordinal));
            if (count < 2)
                return new(false, "canonical_person_not_found");
            _store.Save(state with { WorkspaceCanonicalPersonId = trimmed });
            return new(true, "workspace_bound", CanonicalPersonId: trimmed);
        }
    }

    public IdentityOperationResult StartChallenge(IdentityEndpointKey source)
    {
        if (!source.IsValid())
            return new(false, "invalid_identity_endpoint");

        lock (_gate)
        {
            var state = _store.Load();
            if (!state.Enabled)
                return new(false, "identity_linking_disabled");

            var nowUnix = _utcNow().ToUnixTimeSeconds();
            PruneChallengesLocked(nowUnix);

            var nonce = RandomNumberGenerator.GetBytes(16);
            var payload = $"{source.CanonicalToken()}|{nowUnix}|{Convert.ToHexString(nonce)}";
            var mac = HMACSHA256.HashData(_hmacSecret, Encoding.UTF8.GetBytes(payload));
            var code = "ID-" + Convert.ToHexString(mac)[..16];

            var sourceToken = source.CanonicalToken();
            _challenges.RemoveAll(c => string.Equals(c.Source.CanonicalToken(), sourceToken, StringComparison.Ordinal));
            _challenges.Add(new PendingChallenge(code, source, nowUnix + ChallengeTtlSeconds));
            while (_challenges.Count > MaxPendingChallenges)
            {
                _challenges.RemoveAt(0);
            }

            return new(true, "challenge_issued", code, nowUnix + ChallengeTtlSeconds);
        }
    }

    public IdentityOperationResult ConfirmChallenge(IdentityEndpointKey target, string challengeCode)
    {
        if (!target.IsValid())
            return new(false, "invalid_identity_endpoint");
        var code = (challengeCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code))
            return new(false, "challenge_invalid_or_expired");

        lock (_gate)
        {
            var state = _store.Load();
            if (!state.Enabled)
                return new(false, "identity_linking_disabled");

            var nowUnix = _utcNow().ToUnixTimeSeconds();
            PruneChallengesLocked(nowUnix);

            var targetToken = target.CanonicalToken();
            if (_failedAttempts.TryGetValue(targetToken, out var attempt)
                && nowUnix - attempt.WindowStartUnix <= ChallengeTtlSeconds
                && attempt.Failures >= MaxFailedAttemptsPerWindow)
            {
                return new(false, "challenge_rate_limited");
            }

            var codeBytes = Encoding.UTF8.GetBytes(code);
            var matchedIndex = -1;
            for (var i = 0; i < _challenges.Count; i++)
            {
                var pendingCandidate = _challenges[i];
                if (pendingCandidate.ExpiresAtUnix < nowUnix) continue;
                var candidateBytes = Encoding.UTF8.GetBytes(pendingCandidate.ChallengeCode);
                if (candidateBytes.Length == codeBytes.Length
                    && CryptographicOperations.FixedTimeEquals(candidateBytes, codeBytes))
                {
                    matchedIndex = i;
                }
            }

            if (matchedIndex < 0)
            {
                if (_failedAttempts.TryGetValue(targetToken, out var cur) && nowUnix - cur.WindowStartUnix <= ChallengeTtlSeconds)
                    _failedAttempts[targetToken] = (cur.Failures + 1, cur.WindowStartUnix);
                else
                    _failedAttempts[targetToken] = (1, nowUnix);
                return new(false, "challenge_invalid_or_expired");
            }

            var pending = _challenges[matchedIndex];
            _challenges.RemoveAt(matchedIndex);
            _failedAttempts.Remove(targetToken);

            if (string.Equals(pending.Source.CanonicalToken(), targetToken, StringComparison.Ordinal))
                return new(false, "same_endpoint_forbidden");

            var list = state.Links.ToList();
            var existingSource = list.FirstOrDefault(l => SameEndpoint(l.Endpoint, pending.Source));
            var existingTarget = list.FirstOrDefault(l => SameEndpoint(l.Endpoint, target));

            if (existingTarget is not null && existingSource is not null
                && !string.Equals(existingTarget.CanonicalPersonId, existingSource.CanonicalPersonId, StringComparison.Ordinal))
            {
                return new(false, "endpoint_already_bound_to_other");
            }

            var adding = (existingSource is null ? 1 : 0) + (existingTarget is null ? 1 : 0);
            if (list.Count + adding > MaxLinks)
                return new(false, "identity_link_quota_exceeded");

            var canonicalId = existingSource?.CanonicalPersonId
                ?? existingTarget?.CanonicalPersonId
                ?? "person-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

            list.RemoveAll(l => SameEndpoint(l.Endpoint, pending.Source) || SameEndpoint(l.Endpoint, target));
            list.Add(new IdentityLinkRecord(canonicalId, pending.Source, nowUnix));
            list.Add(new IdentityLinkRecord(canonicalId, target, nowUnix));

            _store.Save(state with { Links = list.ToImmutableArray() });
            return new(true, "identity_linked", CanonicalPersonId: canonicalId);
        }
    }

    public IdentityOperationResult Unlink(IdentityEndpointKey endpoint)
    {
        if (!endpoint.IsValid())
            return new(false, "invalid_identity_endpoint");

        lock (_gate)
        {
            var token = endpoint.CanonicalToken();
            _challenges.RemoveAll(c => string.Equals(c.Source.CanonicalToken(), token, StringComparison.Ordinal));

            var state = _store.Load();
            var match = state.Links.FirstOrDefault(l => SameEndpoint(l.Endpoint, endpoint));
            if (match is null)
                return new(false, "identity_not_linked");

            var list = state.Links.Where(l => !SameEndpoint(l.Endpoint, endpoint)).ToList();
            // 清理因解绑而变成孤立单端的记录，确保单端不再残留旧共享凭据。
            var remainingInGroup = list.Count(l => string.Equals(l.CanonicalPersonId, match.CanonicalPersonId, StringComparison.Ordinal));
            var nextWorkspaceId = state.WorkspaceCanonicalPersonId;
            if (remainingInGroup < 2)
            {
                list.RemoveAll(l => string.Equals(l.CanonicalPersonId, match.CanonicalPersonId, StringComparison.Ordinal));
                if (string.Equals(nextWorkspaceId, match.CanonicalPersonId, StringComparison.Ordinal))
                    nextWorkspaceId = null;
            }

            _store.Save(state with { Links = list.ToImmutableArray(), WorkspaceCanonicalPersonId = nextWorkspaceId });
            return new(true, "identity_unlinked");
        }
    }

    public string? ResolveSharedOwnerScope(IdentityEndpointKey endpoint, IdentityResourceKind resource)
    {
        if (!endpoint.IsValid()) return null;
        lock (_gate)
        {
            var state = _store.Load();
            if (!state.Enabled || !IsResourceShared(state.Preferences, resource))
                return null;
            var rec = state.Links.FirstOrDefault(l => SameEndpoint(l.Endpoint, endpoint));
            if (rec is null) return null;
            // 至少保留两个有效绑定端点才激活共享作用域。
            var peerCount = state.Links.Count(l => string.Equals(l.CanonicalPersonId, rec.CanonicalPersonId, StringComparison.Ordinal));
            return peerCount >= 2 ? rec.CanonicalPersonId : null;
        }
    }

    public string? ResolveWorkspaceSharedScope(IdentityResourceKind resource)
    {
        lock (_gate)
        {
            var state = _store.Load();
            if (!state.Enabled || !IsResourceShared(state.Preferences, resource)
                || string.IsNullOrWhiteSpace(state.WorkspaceCanonicalPersonId))
                return null;
            var bound = state.WorkspaceCanonicalPersonId.Trim();
            var count = state.Links.Count(l => string.Equals(l.CanonicalPersonId, bound, StringComparison.Ordinal));
            return count >= 2 ? bound : null;
        }
    }

    private void PruneChallengesLocked(long nowUnix)
    {
        _challenges.RemoveAll(c => c.ExpiresAtUnix < nowUnix);
        foreach (var k in _failedAttempts.Where(kv => nowUnix - kv.Value.WindowStartUnix > ChallengeTtlSeconds).Select(kv => kv.Key).ToArray())
            _failedAttempts.Remove(k);
    }

    private static bool SameEndpoint(IdentityEndpointKey a, IdentityEndpointKey b)
        => string.Equals(a.CanonicalToken(), b.CanonicalToken(), StringComparison.Ordinal);

    private static bool IsResourceShared(IdentitySharePreferences prefs, IdentityResourceKind resource) => resource switch
    {
        IdentityResourceKind.Todos => prefs.ShareTodos,
        IdentityResourceKind.Knowledge => prefs.ShareKnowledge,
        IdentityResourceKind.Preferences => prefs.SharePreferences,
        _ => false
    };

    private static string DefaultMask(string raw)
        => string.IsNullOrEmpty(raw) ? "***"
            : raw.Length <= 4 ? new string('*', raw.Length)
            : $"{raw[..2]}***{raw[^2..]}";
}
