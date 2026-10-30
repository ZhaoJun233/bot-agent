using System.Runtime.CompilerServices;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

/// <summary>Replay, durable reservation, staging and completion share one singleton-owned admission gate.</summary>
internal sealed class PluginMarketOperations
{
    private static readonly ConditionalWeakTable<PluginPackageService, PluginMarketOperations> Owners = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PluginOperationReceiptStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    internal static PluginMarketOperations For(PluginPackageService packages) => Owners.GetValue(packages, _ => new());

    internal PluginOperationReceiptStore Store(string path, Func<DateTimeOffset> now, Action<string>? log)
    {
        lock (_stores)
        {
            var fullPath = Path.GetFullPath(path);
            if (!_stores.TryGetValue(fullPath, out var store))
                _stores.Add(fullPath, store = new PluginOperationReceiptStore(fullPath, now, log));
            return store;
        }
    }

    internal async Task<ZbaPluginOperationResult> ExecuteAsync(PluginOperationReceiptStore receipts, string keyHash,
        string fingerprint, string caller, ZbaPluginOperation operation, Func<Task<string>> revision,
        string expectedRevision, Func<Task> stage, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var replay = receipts.Find(keyHash, fingerprint, caller);
            if (replay is not null) return replay;
            var before = await revision().ConfigureAwait(false);
            if (before != expectedRevision)
                throw new ZbaBackendException(409, "plugin_revision_conflict", "插件目录已变化，请重新读取后再提交。");
            // Reserve before side effects. A crash never converts an uncertain stage into permission to retry it.
            var uncertain = operation with { Status = "failed", RestartRequired = false, ErrorCode = "plugin_operation_unknown" };
            Persist(uncertain, before);
            try { await stage().ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (ZbaBackendException exception)
            {
                Persist(uncertain with { ErrorCode = exception.Code }, await revision().ConfigureAwait(false));
                throw;
            }
            catch (Exception exception)
            {
                operation = operation with { Status = "failed", RestartRequired = false, ErrorCode = FailureCode(exception) };
            }
            var after = await revision().ConfigureAwait(false);
            Persist(operation, after);
            return new(operation, after);
        }
        finally { _gate.Release(); }

        void Persist(ZbaPluginOperation result, string currentRevision)
        {
            if (!receipts.TryAdd(keyHash, result, currentRevision, caller, fingerprint))
                throw new ZbaBackendException(503, "plugin_operation_unknown", "操作回执暂时无法持久化，请核对操作状态，不要换键重试。");
        }
    }

    private static string FailureCode(Exception exception) => exception switch
    {
        InvalidDataException => "plugin_package_invalid",
        HttpRequestException => "plugin_download_failed",
        IOException or UnauthorizedAccessException => "plugin_storage_failed",
        _ => "plugin_operation_failed"
    };
}
