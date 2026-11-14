using System.Globalization;
using System.Text.Json.Nodes;
using BotAgent.Domain.Qq;

namespace BotAgent.Services.Official;

public sealed partial class OfficialBotGateway
{
    private sealed record RoleEntry(DateTimeOffset Watermark, DateTimeOffset RetainUntil,
        OfficialMemberRoleEvidence? Evidence, CancellationTokenSource Revocation);
    private readonly object _roleGate = new();
    private readonly Dictionary<OfficialIdentityKey, RoleEntry> _memberRoles = new();
    private readonly Dictionary<string, DateTimeOffset> _roleGroupBarriers = new(StringComparer.Ordinal);
    private DateTimeOffset _roleBarrier = DateTimeOffset.MinValue;
    private const int RoleCapacity = 2048;
    private static readonly TimeSpan RoleWindow = TimeSpan.FromMinutes(5);

    private OfficialMemberRoleEvidence? ObserveMemberRole(JsonObject data, OfficialIdentityKey subject, bool fullMode)
    {
        var now = Clock.Now;
        var at = RoleEventTime(data["timestamp"]);
        var role = (data["author"] as JsonObject)?["member_role"] is JsonValue value && value.TryGetValue<string>(out var name)
            ? name switch { "member" => OfficialMemberRole.Member, "admin" => OfficialMemberRole.Admin,
                "owner" => OfficialMemberRole.Owner, _ => OfficialMemberRole.Unknown } : OfficialMemberRole.Unknown;
        lock (_roleGate)
        {
            PruneMemberRoles(now);
            _memberRoles.TryGetValue(subject, out var prior);
            // Invalid time can only revoke. It must not turn receive time into evidence of elevated status.
            if (at is null || at > now || at <= now - RoleWindow)
            {
                ReplaceMemberRole(subject, now, now, null);
                return null;
            }
            if (at <= _roleBarrier || _roleGroupBarriers.TryGetValue(subject.Scope.Conversation!.NativeTargetId, out var barrier) && at <= barrier)
                return null;
            if (prior is not null && (at < prior.Watermark
                || at == prior.Watermark && (prior.Evidence is null || (int)role >= (int)prior.Evidence.Role))) return null;
            if (prior is null && _memberRoles.Count >= RoleCapacity) return null;
            var revocation = new CancellationTokenSource();
            var expires = at.Value + RoleWindow < now + RoleWindow ? at.Value + RoleWindow : now + RoleWindow;
            var evidence = new OfficialMemberRoleEvidence(subject, role,
                fullMode ? "GROUP_MESSAGE_CREATE" : "GROUP_AT_MESSAGE_CREATE", at.Value, now, expires, revocation.Token);
            ReplaceMemberRole(subject, at.Value, now, evidence, revocation);
            return evidence;
        }
    }

    // Retain a revoked entry long enough that a still-fresh old message cannot resurrect its former role.
    private void ReplaceMemberRole(OfficialIdentityKey subject, DateTimeOffset watermark, DateTimeOffset now,
        OfficialMemberRoleEvidence? evidence, CancellationTokenSource? revocation = null)
    {
        if (_memberRoles.Remove(subject, out var prior))
        {
            watermark = LaterRoleTime(watermark, prior.Watermark);
            Revoke(prior);
        }
        if (_memberRoles.Count < RoleCapacity)
            _memberRoles[subject] = new(watermark, now + RoleWindow, evidence, revocation ?? new());
        else revocation?.Dispose();
    }

    private static void Revoke(RoleEntry entry)
    {
        entry.Revocation.Cancel();
        entry.Revocation.Dispose();
    }

    private void PruneMemberRoles(DateTimeOffset now)
    {
        foreach (var key in _memberRoles.Where(pair => pair.Value.RetainUntil <= now).Select(pair => pair.Key).ToArray())
            if (_memberRoles.Remove(key, out var entry)) Revoke(entry);
        foreach (var group in _roleGroupBarriers.Where(pair => pair.Value + RoleWindow <= now).Select(pair => pair.Key).ToArray())
            _roleGroupBarriers.Remove(group);
    }

    private void RemoveMemberRole(JsonObject data)
    {
        var group = ReadIdentityId(data["group_openid"]);
        var member = ReadIdentityId(data["member_openid"]);
        var subject = new OfficialIdentityKey(new(_applicationId, OfficialIdentityKind.GroupMember, NativeConversation(true, group)), member);
        var now = Clock.Now;
        var eventTime = RoleEventTime(data["timestamp"]);
        var at = eventTime is { } time && time <= now ? time : now;
        // Removal uses whole Unix seconds; a message within that second cannot prove it happened later.
        if (data["timestamp"] is JsonValue stamp && stamp.TryGetValue<long>(out _) && eventTime == at)
            at = at.AddTicks(TimeSpan.TicksPerSecond - 1);
        lock (_roleGate)
        {
            PruneMemberRoles(now);
            if (_memberRoles.TryGetValue(subject, out var prior) && at < prior.Watermark) return;
            if (!_memberRoles.ContainsKey(subject) && _memberRoles.Count >= RoleCapacity)
                InvalidateMemberRoles(group, at); // Keep the complete removal barrier when the table is full.
            else ReplaceMemberRole(subject, at, now, null);
        }
    }

    private static DateTimeOffset LaterRoleTime(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private void InvalidateMemberRoles(string? group = null, DateTimeOffset? through = null)
    {
        lock (_roleGate)
        {
            var now = Clock.Now;
            PruneMemberRoles(now);
            var barrier = LaterRoleTime(now, through ?? DateTimeOffset.MinValue);
            if (group is null || _roleGroupBarriers.Count >= 256)
            {
                foreach (var entry in _memberRoles.Values)
                {
                    barrier = LaterRoleTime(barrier, entry.Watermark);
                    Revoke(entry);
                }
                foreach (var prior in _roleGroupBarriers.Values) barrier = LaterRoleTime(barrier, prior);
                _memberRoles.Clear();
                _roleGroupBarriers.Clear();
                _roleBarrier = LaterRoleTime(_roleBarrier, barrier);
                return;
            }
            foreach (var key in _memberRoles.Keys.Where(key => key.Scope.Conversation!.NativeTargetId == group).ToArray())
                if (_memberRoles.Remove(key, out var entry))
                {
                    barrier = LaterRoleTime(barrier, entry.Watermark);
                    Revoke(entry);
                }
            if (_roleGroupBarriers.TryGetValue(group, out var priorBarrier)) barrier = LaterRoleTime(barrier, priorBarrier);
            _roleGroupBarriers[group] = barrier;
        }
    }

    private static DateTimeOffset? RoleEventTime(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return value.TryGetValue<string>(out var text) && text.Contains('T')
            && (text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
    }

    private static string DisplayName(JsonObject data, long alias)
        => (data["author"] as JsonObject)?["username"] is JsonValue value && value.TryGetValue<string>(out var name)
            && !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && !name.Any(char.IsControl)
            ? name.Trim() : "官方用户" + alias.ToString(CultureInfo.InvariantCulture);
}
