using BotAgent.Domain.Memory;
using BotAgent.Services.Memory;
using BotAgent.Services.Reply;

namespace BotAgent.MemoryTurnProbe;

internal static class CapacityChecks
{
    public static void Run()
    {
        using var f = new Fixture();
        f.Remember();
        var envelope = f.Envelope();
        using var execution = f.Enter(envelope);
        using var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope);
        Probe.Check(turn.ReadPrompt("synthetic query").Length > 0, "capacity fixture first ticket is consumed by a real turn");
        f.Time.Advance(TimeSpan.FromMinutes(1));
        var last = default(UnifiedMemoryReceipt);
        var issued = 0;
        for (var i = 0; i < 1023; i++)
        {
            last = f.Memory.AuthorizeLegacyProfile(Fixture.Group, Fixture.Actor);
            if (last is not null) issued++;
        }
        Probe.Check(issued == 1023, "mixed memory receipt cache reaches exact 1024 capacity");
        Probe.Check(f.Memory.AuthorizeLegacyProfile(Fixture.Group, Fixture.Actor) is null,
            "full active cache rejects new legacy ticket");
        var denied = f.Memory.Read(Fixture.Group, "synthetic query");
        Probe.Check(denied.Receipt is null && denied.Entries.IsEmpty, "full active cache never exposes unreceipted unified facts");
        Probe.Check(Probe.Model() && Probe.Send(Fixture.Group), "capacity pressure preserves existing active turn ticket");
        Probe.Check(last is not null && f.Memory.ValidateReceipt(last, Fixture.Group), "capacity pressure preserves newest legacy ticket");
        f.Time.Advance(TimeSpan.FromMinutes(9));
        var replacement = f.Memory.Read(Fixture.Group, "synthetic query");
        Probe.Check(replacement.Receipt is not null && !replacement.Entries.IsEmpty, "exact TTL boundary reclaims expired ticket for new read");
        Probe.Check(last is not null && f.Memory.ValidateReceipt(last, Fixture.Group), "expiry reclamation retains younger active legacy tickets");
        Probe.Check(!Probe.Model() && !Probe.Send(Fixture.Group), "expired consumed turn ticket rejects subsequent model and send");
        f.Time.Advance(TimeSpan.FromMinutes(1));
        Probe.Check(f.Memory.AuthorizeLegacyProfile(Fixture.Group, Fixture.Actor) is not null,
            "subsequent expiry reclamation restores legacy issuance");
        Probe.Check(replacement.Receipt is not null && f.Memory.ValidateReceipt(replacement.Receipt, Fixture.Group),
            "legacy capacity reclamation retains younger unified receipt");
    }
}
