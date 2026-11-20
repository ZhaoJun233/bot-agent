using BotAgent.Adapters.Panel;
using BotAgent.Adapters.Panel.Zba;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Management;
using BotAgent.Services;

namespace BotAgent.Host;

internal static partial class CompositionRoot
{
    internal sealed class BusinessStartupCleanupException(Exception startupFailure, Exception cleanupFailure)
        : AggregateException("business_startup_cleanup_incomplete", startupFailure, cleanupFailure);

    internal sealed class ControlGraph : IAsyncDisposable
    {
        internal readonly Assembly Assembly;
        internal readonly IManagedAuthorization ManagedAuthorization;
        internal int BusinessBuildStarted;
        private int _disposed;

        internal ControlGraph(Assembly assembly, IManagedAuthorization authorization)
        {
            Assembly = assembly;
            ManagedAuthorization = authorization;
            assembly.PluginRuntime.RuntimeAdoptionEnabled = false;
            Plane = new(assembly.Configuration, assembly.Authorization, authorization, assembly.PluginRuntime.Market,
                () => BotAgent.Adapters.Time.Clock.Now, BotConfig.HadStoredSettingsAtLoad);
            assembly.ControlPlane = Plane;
            Server = new(assembly.Settings.HealthPort, Plane.Endpoint);
        }

        internal ZbaControlPlane Plane { get; }
        internal ControlPlaneHttpServer Server { get; }

        internal void CompleteStartup(AppGraph? business)
        {
            if (business is not null)
            {
                if (business.Zba is null) throw new InvalidOperationException("business_zba_missing");
                Server.AttachBusiness(business.Web);
                Assembly.PluginRuntime.RuntimeAdoptionEnabled = true;
            }
            Plane.Backend.CompleteStartup(business?.Zba?.Backend);
        }

        internal void DetachBusiness()
        {
            Assembly.PluginRuntime.RuntimeAdoptionEnabled = false;
            Server.DetachBusiness();
            Plane.Backend.DetachBusiness();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            DetachBusiness();
            Server.Dispose();
            await Assembly.PluginRuntime.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static ControlGraph BuildControl(AppSettings settings, IManagedAuthorization authorization)
    {
        InitializeAssemblyPrerequisites(settings);
        var assembly = new Assembly(settings);
        AssembleControlCore(assembly);
        return new(assembly, authorization);
    }

    internal static async Task<AppGraph> BuildBusinessAsync(ControlGraph control)
    {
        if (Interlocked.Exchange(ref control.BusinessBuildStarted, 1) != 0)
            throw new InvalidOperationException("business_build_requires_restart");
        var assembly = control.Assembly;
        try
        {
            AssembleBusinessCore(assembly);
            AssembleChannels(assembly);
            AssembleModel(assembly);
            AssembleStores(assembly);
            AssembleTools(assembly);
            AssembleReply(assembly);
            AssembleSurface(assembly, control.ManagedAuthorization);
            return assembly.Complete() with { OwnsPluginRuntime = false };
        }
        catch (Exception constructionFailure)
        {
            try { await DisposeIncompleteBusinessAsync(assembly).ConfigureAwait(false); }
            catch (Exception cleanupFailure) { throw new BusinessStartupCleanupException(constructionFailure, cleanupFailure); }
            throw;
        }
    }

    private static async Task DisposeIncompleteBusinessAsync(Assembly a)
    {
        var failures = new List<Exception>();
        try { a.Web?.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { if (a.Background is not null) await a.Background.StopAsync(); } catch (Exception error) { failures.Add(error); }
        try { if (a.Mcp?.Runtime is { } mcp) await mcp.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        try { if (a.Zba is not null) await a.Zba.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        try { a.HealthReports?.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { a.Backups?.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { if (a.Management is not null) await a.Management.StopAsync(); } catch (Exception error) { failures.Add(error); }
        try { if (a.Management is not null) await a.Management.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        try { a.Agent?.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { (a.Source as IDisposable)?.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("business_cleanup_incomplete", failures);
    }
}
