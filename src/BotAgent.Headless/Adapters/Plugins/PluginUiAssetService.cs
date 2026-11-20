using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;
using BotAgent.Services.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>
/// Read-only, hash-verified runtime UI asset delivery and generation-bound UI session leases.
/// Serves assets strictly from approved release ZIP archives (Active or Previous) with strict CSP;
/// never serves unapproved paths, tampered bytes, or host HTML fallbacks.
/// </summary>
public sealed class PluginUiAssetService
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; " +
        "img-src 'self' data:; connect-src 'none'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";

    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(30);
    private readonly PluginPackageService _packages;
    private readonly PluginManager _registry;
    private readonly Func<DateTimeOffset> _now;
    private readonly byte[] _ticketKey = RandomNumberGenerator.GetBytes(32);

    public PluginUiAssetService(PluginPackageService packages, PluginManager registry, Func<DateTimeOffset> now)
    {
        _packages = packages;
        _registry = registry;
        _now = now;
    }

    public async ValueTask<ZbaPluginUiSession> OpenSessionAsync(string pluginId, CancellationToken ct)
    {
        PluginPackageValidation.ValidateId(pluginId, allowReserved: true);
        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var pkg = installed.FirstOrDefault(item => item.Id == pluginId);
        if (pkg?.MetadataFailure is not null)
            throw new ZbaBackendException(409, pkg.MetadataFailure.Code, "插件元数据损坏，无法加载运行界面。");
        var active = pkg?.Active;
        if (active?.Ui is not { } ui)
            throw new ZbaBackendException(404, "plugin_ui_not_available", "该插件未声明受控运行界面。");

        var releaseSha = active.Sha256.ToLowerInvariant();
        var loaded = _registry.GetAll().FirstOrDefault(item => item.Id == pluginId);
        var backendReady = loaded is not null && ((loaded.IsEnabled && _registry.IsReady(pluginId))
            || (active.Control && _registry.IsControlReady(pluginId)));
        var versionMatched = loaded is not null && string.Equals(loaded.Version, active.Version, StringComparison.Ordinal);
        PluginUiSessionLease lease;
        try { lease = _registry.OpenUiSession(pluginId, releaseSha); }
        catch (InvalidOperationException ex) when (ex.Message == "plugin_registry_stopped")
        { throw new ZbaBackendException(503, "plugin_ui_unavailable", "插件运行时已停止，无法打开界面。"); }
        var expiresAt = _now().Add(TicketLifetime);
        var ticket = MintTicket(pluginId, releaseSha, lease.SessionId, expiresAt);
        var entryUrl = $"/zba/v1/plugins/{Uri.EscapeDataString(pluginId)}/ui/{releaseSha}/{ticket}/{ui.Entry}";
        var adoptionMode = PluginUiValidation.ResolveAdoptionMode(active);
        var allowedActions = (ui.AllowedActions ?? [])
            .Where(action => PluginUiValidation.IsActionAllowedForPlugin(pluginId, action))
            .ToArray();

        return new ZbaPluginUiSession(
            lease.SessionId,
            pluginId,
            active.Version,
            releaseSha,
            lease.GenerationId,
            ui.ContractVersion,
            entryUrl,
            allowedActions,
            backendReady,
            versionMatched,
            adoptionMode,
            ui.Kind ?? "dialog",
            ui.Title ?? pluginId,
            expiresAt);
    }

    public ValueTask<ZbaPluginUiSessionClosed> CloseSessionAsync(string pluginId, string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PluginPackageValidation.ValidateId(pluginId, allowReserved: true);
        var closed = _registry.CloseUiSession(sessionId, pluginId);
        return ValueTask.FromResult(new ZbaPluginUiSessionClosed(sessionId, pluginId, closed));
    }

    public async ValueTask<ZbaPluginUiAssetResult> ReadAssetAsync(
        string pluginId, string releaseSha, string ticket, string assetPath, bool authenticatedCaller, CancellationToken ct)
    {
        try { PluginPackageValidation.ValidateId(pluginId, allowReserved: true); }
        catch (InvalidDataException) { return NotFound(); }

        var normalizedSha = releaseSha.ToLowerInvariant();
        if (normalizedSha.Length != 64 || !normalizedSha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return NotFound();

        if (!authenticatedCaller && !VerifyTicket(ticket, pluginId, normalizedSha))
            return Unauthorized();

        if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("web/", StringComparison.Ordinal))
            return NotFound();
        try { PluginPackageValidation.ValidateRelativePath(assetPath); }
        catch (InvalidDataException) { return NotFound(); }

        var pkg = _packages.InstalledSnapshot.FirstOrDefault(item => item.Id == pluginId);
        if (pkg is null)
        {
            var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
            pkg = installed.FirstOrDefault(item => item.Id == pluginId);
        }
        if (pkg is null) return NotFound();

        // Serve immutable assets for both Active and Previous so open pages can finish read-only recovery.
        var manifest = new[] { pkg.Active, pkg.Previous }
            .FirstOrDefault(m => m is not null && m.Sha256.Equals(normalizedSha, StringComparison.OrdinalIgnoreCase));
        if (manifest is null)
        {
            var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
            pkg = installed.FirstOrDefault(item => item.Id == pluginId);
            manifest = pkg is null ? null : new[] { pkg.Active, pkg.Previous }
                .FirstOrDefault(m => m is not null && m.Sha256.Equals(normalizedSha, StringComparison.OrdinalIgnoreCase));
        }
        if (manifest?.Ui is not { } ui) return NotFound();

        var declared = ui.Assets.FirstOrDefault(a => string.Equals(a.Path, assetPath, StringComparison.Ordinal));
        if (declared is null) return NotFound();

        byte[] archiveBytes;
        try { archiveBytes = await _packages.ReadReleaseArchiveAsync(manifest, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return NotFound(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        { return IntegrityMismatch(); }

        var archiveDigest = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        if (!archiveDigest.Equals(normalizedSha, StringComparison.OrdinalIgnoreCase))
            return IntegrityMismatch();

        using var zip = new ZipArchive(new MemoryStream(archiveBytes, writable: false), ZipArchiveMode.Read);
        var entry = zip.GetEntry(declared.Path);
        if (entry is null || entry.Length > PluginUiValidation.MaxTotalUiBytes) return IntegrityMismatch();

        await using var stream = entry.Open();
        using var buffer = new MemoryStream((int)Math.Min(entry.Length, PluginUiValidation.MaxTotalUiBytes));
        var chunk = new byte[16384];
        long copied = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            copied += read;
            if (copied > PluginUiValidation.MaxTotalUiBytes) return IntegrityMismatch();
            buffer.Write(chunk, 0, read);
        }
        var assetBytes = buffer.ToArray();
        var assetDigest = Convert.ToHexString(SHA256.HashData(assetBytes)).ToLowerInvariant();
        if (!assetDigest.Equals(declared.Sha256, StringComparison.OrdinalIgnoreCase))
            return IntegrityMismatch();

        return new ZbaPluginUiAssetResult(200, declared.ContentType, assetBytes, null, ContentSecurityPolicy,
            "private, max-age=31536000, immutable");
    }

    private string MintTicket(string pluginId, string releaseSha, string sessionId, DateTimeOffset expiresAt)
    {
        var exp = expiresAt.ToUnixTimeSeconds();
        var payload = $"{pluginId}|{releaseSha}|{sessionId}|{exp}";
        var sig = ComputeHmac(payload);
        return $"{sessionId}.{exp}.{sig}";
    }

    private bool VerifyTicket(string ticket, string pluginId, string releaseSha)
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket == "auth") return false;
        var parts = ticket.Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[1], out var exp)) return false;
        if (_now().ToUnixTimeSeconds() > exp) return false;
        if (!_registry.HasOpenUiSession(parts[0], pluginId, releaseSha)) return false;
        var expected = ComputeHmac($"{pluginId}|{releaseSha}|{parts[0]}|{exp}");
        var actualBytes = Encoding.ASCII.GetBytes(parts[2]);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private string ComputeHmac(string payload)
        => Convert.ToHexString(HMACSHA256.HashData(_ticketKey, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    private static ZbaPluginUiAssetResult NotFound()
        => new(404, "application/json; charset=utf-8", Array.Empty<byte>(), "plugin_ui_asset_not_found", null, "no-store");

    private static ZbaPluginUiAssetResult Unauthorized()
        => new(401, "application/json; charset=utf-8", Array.Empty<byte>(), "plugin_ui_ticket_invalid", null, "no-store");

    private static ZbaPluginUiAssetResult IntegrityMismatch()
        => new(409, "application/json; charset=utf-8", Array.Empty<byte>(), "plugin_ui_asset_integrity_mismatch", null, "no-store");
}
