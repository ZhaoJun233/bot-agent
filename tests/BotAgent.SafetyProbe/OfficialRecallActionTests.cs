using System.Text.Json.Nodes;
using BotAgent.Domain.Platforms;
using BotAgent.Services.Agent;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void OfficialRecallActionTests()
    {
        var numeric = new OfficialActionFixture(true);
        numeric.Box.Apply(s => s.AgentServerQqActions = "recall");
        Check("official recall accepts an exact JSON integer alias", numeric.Recall(new JsonObject { ["message_id"] = 8000000000000003L }).Ok
            && numeric.Source.RecalledMessage == 8000000000000003 && numeric.Source.RecallCalls == 1);
        foreach (var group in new[] { true, false })
        {
            var f = new OfficialActionFixture(group);
            f.Box.Apply(s => s.AgentServerQqActions = ""); // Existing default-safe recall, never send.
            var args = new JsonObject { ["message_id"] = "8000000000000003" };
            var success = f.Recall(args);
            Check("official default recall uses captured source and exact alias", success.Ok && f.Source.RecallCalls == 1
                && f.Source.Calls == 0 && f.Source.Group == group && f.Source.Target == 8000000000000001
                && f.Source.RecalledMessage == 8000000000000003 && f.Source.Lease == f.Lease);
            foreach (var invalid in new[] { new JsonObject(), new JsonObject { ["message_id"] = "this" },
                new JsonObject { ["message_id"] = 1.5 },
                new JsonObject { ["message_id"] = "10001" }, new JsonObject { ["message_id"] = "8000000000000003", ["role"] = "admin" },
                new JsonObject { ["message_id"] = "8000000000000003", ["target_id"] = "8000000000000002" },
                new JsonObject { ["message_id"] = "8000000000000003", ["app_id"] = "other" } })
                Check("official recall rejects forged arguments before source call", !f.Recall(invalid).Ok && f.Source.RecallCalls == 1);
            f.Source.RecallResult = DeliveryResult.Unknown("delivery_outcome_unknown");
            var unknown = f.Recall(args);
            Check("official recall preserves Unknown for Agent stop rule", !unknown.Ok && unknown.ReasonCode == "delivery_unknown"
                && f.Source.RecallCalls == 2 && unknown.ModelText?.Contains("不要自动重试") == true);
            f.Registry.Snapshot = f.Registry.Snapshot with { Capabilities = f.Registry.Snapshot.Capabilities with { SupportsRecall = false } };
            Check("recall capability revocation removes prompt and execution", !f.Host.CanUseAction(QqActionCatalog.Find("recall")!)
                && !f.Recall(args).Ok && f.Source.RecallCalls == 2);
        }
    }
}
