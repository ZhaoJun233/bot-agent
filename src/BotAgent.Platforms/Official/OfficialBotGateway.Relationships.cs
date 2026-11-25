using System.Text.Json.Nodes;

namespace BotAgent.Services.Official;

public sealed partial class OfficialBotGateway
{
    private void RelationshipChanged(bool isGroup, bool removed, JsonObject data)
    {
        // Validate the documented target without allocating an alias or trusting union identity fields.
        var target = TargetIdentity(isGroup, ReadIdentityId(data[isGroup ? "group_openid" : "openid"])).NativeId;
        if (isGroup)
        {
            InvalidateGroupSelf(target);
            InvalidateMemberRoles(target);
        }
        InvalidateRelationshipReferences(isGroup, target);
        if (removed) RevokeDeliveryScope(isGroup, target);
        Log($"official_relationship scope={(isGroup ? "group" : "direct")} state={(removed ? "removed" : "added")}");
    }

    private void NotificationChanged(bool isGroup, JsonObject data)
    {
        _ = TargetIdentity(isGroup, ReadIdentityId(data[isGroup ? "group_openid" : "openid"]));
        // Active delivery is disabled. A notification event cannot grant it or revoke passive credentials.
        Log($"official_notification scope={(isGroup ? "group" : "direct")} active_delivery=disabled");
    }
}
