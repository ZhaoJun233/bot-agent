using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;
using BotAgent.Services.Net;

namespace BotAgent.Adapters.Plugins;

/// <summary>Single acquisition policy authority. Refresh publication and package commit share a gate.</summary>
public sealed class PluginGovernanceService
{
    private static readonly TimeSpan CheckedTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromDays(7);
    private readonly Func<DateTimeOffset> _clock;
    private readonly PluginCatalogSource _source;
    private readonly PluginGovernanceCacheStore _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private PluginGovernanceDocument? _document;
    private ZbaPluginGovernanceSource _status;

    public PluginGovernanceService(string root, IHttpFetcher http, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => Clock.UtcNow);
        _source = new(http);
        _cache = new(root);
        _status = new("unavailable", SourceUrl(), null, null, "plugin_governance_unavailable");
        try
        {
            if (_cache.Load(PluginGovernanceDocument.MaxCacheBytes) is { } cached)
            {
                _document = PluginGovernanceDocument.Parse(cached, cache: true);
                _status = new("cached", SourceUrl(), _document.GeneratedAt, null, null);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or PluginCatalogException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { _status = _status with { ErrorCode = "plugin_governance_cache_invalid" }; }
    }

    private static string SourceUrl() => Uri.TryCreate(PluginCatalogSource.ConfiguredUrl(), UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        ? new Uri(uri, "governance.json").AbsoluteUri : "governance.json";

    public ZbaPluginGovernanceSource Source { get { lock (_stateLock) return EffectiveSourceLocked(); } }

    private ZbaPluginGovernanceSource EffectiveSourceLocked()
    {
        if (_status.Status != "fresh") return _status;
        var now = _clock();
        if (_status.CheckedAt is null || _status.GeneratedAt is null ||
            now - PluginGovernanceDocument.Timestamp(_status.CheckedAt) >= CheckedTtl ||
            now - PluginGovernanceDocument.Timestamp(_status.GeneratedAt) > SnapshotMaxAge)
            return _status with { Status = "cached", ErrorCode = "plugin_governance_expired" };
        return _status;
    }

    public ZbaPluginGovernance Evaluate(PluginPackageManifest manifest)
    {
        lock (_stateLock)
        {
            var records = _document?.Entries ?? [];
            bool Identity(PluginGovernanceRecord r) => r.Id == manifest.Id && r.Version == manifest.Version &&
                string.Equals(r.Sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase);
            var record = records.FirstOrDefault(r => r.Status == "revoked" && Identity(r)) ??
                records.FirstOrDefault(r => r.Status == "revoked" && string.Equals(r.Sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase)) ??
                records.FirstOrDefault(Identity);
            return record is null
                ? new(EffectiveSourceLocked().Status == "fresh" ? "active" : "unknown", null, null, false, "none")
                : new(record.Status, record.Reason, record.UpdatedAt, record.Status == "revoked", Identity(record) ? "identity" : "sha256");
        }
    }

    public void EnsureAllowed(PluginPackageManifest manifest)
    {
        if (Evaluate(manifest).Blocked) throw new ZbaBackendException(409, "plugin_governance_revoked", "该插件包已被安全撤回，禁止获取；现有安装保持不变。");
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var source = SourceUrl();
            var now = _clock();
            var checkedAt = now.UtcDateTime.ToString("O");
            try
            {
                var bytes = await _source.FetchAsync(source, ct).ConfigureAwait(false);
                var incoming = PluginGovernanceDocument.Parse(bytes);
                var snapshotTime = PluginGovernanceDocument.Timestamp(incoming.GeneratedAt);
                if (snapshotTime > now.AddMinutes(5)) throw PluginGovernanceDocument.Invalid();
                var aged = now - snapshotTime > SnapshotMaxAge;
                PluginGovernanceDocument merged;
                lock (_stateLock)
                {
                    var prior = _document;
                    if (prior is not null && PluginGovernanceDocument.Timestamp(incoming.GeneratedAt) < PluginGovernanceDocument.Timestamp(prior.GeneratedAt))
                        throw new InvalidDataException("plugin_governance_stale");
                    var retained = prior?.Entries.Where(r => aged || r.Status == "revoked").ToArray() ?? [];
                    var keys = retained.Select(r => (r.Id, r.Version, r.Sha256)).ToHashSet();
                    merged = incoming with { Entries = incoming.Entries.Where(r => !keys.Contains((r.Id, r.Version, r.Sha256))).Concat(retained).ToArray() };
                    if (prior is not null && snapshotTime == PluginGovernanceDocument.Timestamp(prior.GeneratedAt) &&
                        !merged.Entries.OrderBy(r => r.Id).ThenBy(r => r.Version).ThenBy(r => r.Sha256).SequenceEqual(
                            prior.Entries.OrderBy(r => r.Id).ThenBy(r => r.Version).ThenBy(r => r.Sha256)))
                        throw new InvalidDataException("plugin_governance_stale");
                    // Publish new deny records even if persistence fails; never reopen acquisition in this process.
                    _document = merged;
                }
                var cached = JsonSerializer.SerializeToUtf8Bytes(merged, PluginGovernanceDocument.Json);
                if (cached.Length > PluginGovernanceDocument.MaxCacheBytes || merged.Entries.Length > 131072)
                    throw new IOException("plugin_governance_cache_full");
                await _cache.SaveAsync(cached, ct).ConfigureAwait(false);
                lock (_stateLock) _status = new(aged ? "cached" : "fresh", source, incoming.GeneratedAt, checkedAt,
                    aged ? "plugin_governance_expired" : null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is InvalidDataException or PluginCatalogException or IOException or UnauthorizedAccessException or HttpRequestException or JsonException or ArgumentException or InvalidOperationException or TaskCanceledException)
            {
                var code = ex.Message == "plugin_governance_stale" ? "plugin_governance_stale" : "plugin_governance_unavailable";
                lock (_stateLock) _status = new(_document is null ? "unavailable" : "cached", source, _document?.GeneratedAt, checkedAt, code);
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task<IDisposable> AcquireCommitAsync(PluginPackageManifest manifest, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { EnsureAllowed(manifest); return new CommitLease(_gate); }
        catch { _gate.Release(); throw; }
    }
    private sealed class CommitLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
