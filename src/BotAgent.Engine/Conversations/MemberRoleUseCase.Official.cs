using BotAgent.Domain.Qq;
using BotAgent.Engine.Runtime;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Conversations;

public sealed partial class MemberRoleUseCase
{
    private sealed record ObservedOfficialMember(string SourceKey, string Name, OfficialMemberRoleEvidence Evidence,
        CancellationToken Lifetime, PlatformSourceLease? Source);
    private readonly object _officialGate = new();
    private readonly Dictionary<OfficialIdentityKey, ObservedOfficialMember> _official = new();

    private static bool Fresh(ObservedOfficialMember member)
        => member.Evidence.IsFresh(EngineClock.Now) && !member.Lifetime.IsCancellationRequested
            && (member.Source is null || member.Source.IsCurrent);

    private void RememberOfficial(QqChatMessage message, string sourceKey)
    {
        if (!Channels.IsOfficial(message.Channel) || !message.IsGroup || message.IsReliableIdentity != true
            || message.OfficialRole is not { } evidence || !evidence.IsFresh(EngineClock.Now)) return;
        lock (_officialGate)
        {
            PruneOfficial();
            if (_official.Count >= 2048 && !_official.ContainsKey(evidence.Subject)) return;
            _official[evidence.Subject] = new(sourceKey, message.SenderName,
                evidence, message.SourceLifetime, PlatformSourceExecution.Current);
        }
    }

    private void PruneOfficial()
    {
        foreach (var key in _official.Where(pair => !Fresh(pair.Value)).Select(pair => pair.Key).ToArray()) _official.Remove(key);
    }

    public string? DescribeForPrompt(string sourceKey, long groupId, int limit, out int count)
    {
        if (!Channels.IsOfficial(Channels.ChannelOf(sourceKey))) return DescribeForPrompt(groupId, limit, out count);
        lock (_officialGate)
        {
            PruneOfficial();
            var source = PlatformSourceExecution.Current;
            var members = _official.Values.Where(member => member.SourceKey == sourceKey
                && member.Source?.Origin.Driver == source?.Origin.Driver && member.Source?.Lifetime == source?.Lifetime
                && member.Evidence.Role is OfficialMemberRole.Admin or OfficialMemberRole.Owner)
                .OrderByDescending(member => member.Evidence.ObservedAt).Take(Math.Clamp(limit, 0, 14)).ToArray();
            count = members.Length;
            return count == 0 ? null : "近期官方群角色（仅供称呼，不授予审批或工具权限）："
                + string.Join("；", members.Select(member => member.Name + "："
                    + (member.Evidence.Role == OfficialMemberRole.Owner ? "群主" : "管理员")));
        }
    }
}
