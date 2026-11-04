using BotAgent.Panel;
using BotAgent.Domain.Configuration;
namespace BotAgent.ZbaProbe;

internal sealed partial class SyntheticZbaBackend
{
    private static ZbaQuota Quota(string revision = "0", int limit = 50000) => new("group:10001", "synthetic", revision,
        limit, 0, 0, 0, limit, "2026-01-02", "2026-01-03T00:00:00.0000000+00:00", false, 1, 1000000000);
    public ValueTask<ZbaQuotaPage> ListQuotasAsync(ConfigurationCaller caller, CancellationToken ct = default)
        => ValueTask.FromResult(new ZbaQuotaPage(new[] { Quota() }));
    public ValueTask<ZbaQuotaSaveResult> SaveQuotaAsync(ConfigurationCaller caller, ZbaQuotaSaveRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(new ZbaQuotaSaveResult(Quota("1", request.DailyTokenLimit)));
    public ValueTask<ZbaQuotaFindResult> FindQuotaAsync(ConfigurationCaller caller, string key, CancellationToken ct = default)
        => ValueTask.FromResult(new ZbaQuotaFindResult(true, Quota("1", 70)));
}
