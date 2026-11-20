using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins;

public sealed partial class PluginManager
{
    /// <summary>Host composition selects an explicit provider and shared contract. Every invocation is leased.</summary>
    public T GetContribution<T>(string providerId) where T : class, IPluginContribution
        => Contribution<T>(providerId, null);

    /// <summary>Atomically resolves a ready contribution without TOCTOU between IsReady and GetContribution.</summary>
    public bool TryGetContribution<T>(string providerId, out T? contribution) where T : class, IPluginContribution
    {
        var contract = typeof(T);
        ValidateContributionContract(contract);
        Entry? provider;
        lock (_sync)
        {
            if (!_plugins.TryGetValue(providerId, out provider) || !Ready(provider, new()))
            {
                contribution = null;
                return false;
            }
        }
        var gate = new InvocationGate(this, provider, control: false);
        var proxy = DispatchProxy.Create<T, PluginContributionProxy>();
        ((PluginContributionProxy)(object)proxy).Dispatch = (method, args) => InvokeContribution(provider, gate, contract, method, args, control: false);
        contribution = proxy;
        return true;
    }

    /// <summary>Atomically resolves a control-ready contribution without TOCTOU between IsControlReady and GetControlContribution.</summary>
    public bool TryGetControlContribution<T>(string providerId, out T? contribution) where T : class, IPluginControlContribution
    {
        var contract = typeof(T);
        ValidateContributionContract(contract);
        Entry? provider;
        lock (_sync)
        {
            if (!_plugins.TryGetValue(providerId, out provider) || !ControlReady(provider))
            {
                contribution = null;
                return false;
            }
        }
        var gate = new InvocationGate(this, provider, control: true);
        var proxy = DispatchProxy.Create<T, PluginContributionProxy>();
        ((PluginContributionProxy)(object)proxy).Dispatch = (method, args) => InvokeContribution(provider, gate, contract, method, args, control: true);
        contribution = proxy;
        return true;
    }

    /// <summary>Discovers all currently ready contributions implementing <typeparamref name="T"/> in deterministic plugin ID order.</summary>
    public IReadOnlyList<T> GetContributions<T>() where T : class, IPluginContribution
    {
        var contract = typeof(T);
        ValidateContributionContract(contract);
        List<Entry> readyEntries;
        lock (_sync)
        {
            readyEntries = _plugins.Values
                .Where(entry => Ready(entry, new()))
                .OrderBy(entry => entry.Info.Id, StringComparer.Ordinal)
                .ToList();
        }

        var results = new List<T>();
        foreach (var entry in readyEntries)
        {
            var gate = new InvocationGate(this, entry, control: false);
            using var lease = gate.TryAcquire();
            if (lease is null) continue;
            object? target;
            try
            {
                var plugin = entry.Plugin;
                target = plugin is IPluginContributionSource source ? source.GetContribution(contract) : plugin;
            }
            catch
            {
                continue;
            }

            if (target is null || !contract.IsInstanceOfType(target)) continue;
            var provider = entry;
            var proxy = DispatchProxy.Create<T, PluginContributionProxy>();
            ((PluginContributionProxy)(object)proxy).Dispatch = (method, args) => InvokeContribution(provider, gate, contract, method, args, control: false);
            results.Add(proxy);
        }

        return results;
    }

    /// <summary>A consumer can call only its declared, exact direct dependency generation.</summary>
    public T GetDependencyContribution<T>(string consumerId, string providerId) where T : class, IPluginContribution
        => Contribution<T>(providerId, consumerId);

    public T GetControlContribution<T>(string providerId) where T : class, IPluginControlContribution
        => Contribution<T>(providerId, null, control: true);

    /// <summary>Host endpoint binding: resolve a fresh, leased control handle for each independent request.
    /// Never exposed through package contexts; package-held handles remain fixed to their activation.</summary>
    public T CreateControlEntryPoint<T>(string providerId) where T : class, IPluginControlContribution
    {
        ValidateContributionContract(typeof(T));
        var proxy = DispatchProxy.Create<T, PluginContributionProxy>();
        ((PluginContributionProxy)(object)proxy).Dispatch = (method, args) =>
        {
            var current = GetControlContribution<T>(providerId);
            try { return method.Invoke(current, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        };
        return proxy;
    }

    private T Contribution<T>(string providerId, string? consumerId, Entry? fixedConsumer = null,
        bool control = false, bool controlCaller = false) where T : class, IPluginContribution
    {
        var contract = typeof(T);
        ValidateContributionContract(contract);
        Entry provider;
        Entry caller;
        lock (_sync)
        {
            if (!_plugins.TryGetValue(providerId, out provider!) || !(control ? ControlReady(provider) : Ready(provider, new())))
                throw new InvalidOperationException("plugin_contribution_unavailable");
            caller = provider;
            if (consumerId is not null && (!_plugins.TryGetValue(consumerId, out caller!)
                || !(controlCaller ? ControlReady(caller) : Ready(caller, new())) || !caller.Dependencies.Contains(provider)))
                throw new InvalidOperationException("plugin_dependency_unavailable");
            if (fixedConsumer is not null && !ReferenceEquals(caller, fixedConsumer))
                throw new InvalidOperationException("plugin_dependency_unavailable");
        }
        var gate = new InvocationGate(this, caller, control || controlCaller);
        var proxy = DispatchProxy.Create<T, PluginContributionProxy>();
        ((PluginContributionProxy)(object)proxy).Dispatch = (method, args) => InvokeContribution(provider, gate, contract, method, args, control);
        return proxy;
    }

    private static void ValidateContributionContract(Type contract)
    {
        if (!contract.IsInterface || (contract == typeof(IPluginContribution) || contract == typeof(IPluginControlContribution))
            || AssemblyLoadContext.GetLoadContext(contract.Assembly) != AssemblyLoadContext.Default
            || contract.GetInterfaces().Append(contract).SelectMany(t => t.GetMethods())
                .Any(m => m.IsGenericMethodDefinition
                    || !PluginContributionProxy.SupportedResult(m.ReturnType)
                    || m.GetParameters().Any(p => p.ParameterType.IsByRefLike || p.ParameterType.IsPointer)))
            throw new InvalidOperationException("plugin_contribution_contract_invalid");
    }

    private object? InvokeContribution(Entry provider, InvocationGate gate, Type contract, MethodInfo method, object?[]? args, bool control)
    {
        var lease = gate.TryAcquire() ?? throw new InvalidOperationException("plugin_contribution_unavailable");
        _callbackDepth.Value++;
        try
        {
            var plugin = provider.Plugin;
            var target = control ? (plugin as IPluginControlSource)?.GetControlContribution(contract)
                : plugin is IPluginContributionSource source ? source.GetContribution(contract) : plugin;
            if (target is null || !contract.IsInstanceOfType(target))
                throw new InvalidOperationException("plugin_contribution_missing");
            object? result;
            try { result = method.Invoke(target, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            return PluginContributionProxy.Complete(method.ReturnType, result, lease);
        }
        catch { lease.Dispose(); throw; }
        finally { _callbackDepth.Value--; }
    }
}

/// <summary>Runtime-generated interface adapter; asynchronous completion owns the full invocation lease.</summary>
public class PluginContributionProxy : DispatchProxy
{
    internal static bool SupportedResult(Type type)
    {
        if (type == typeof(Task) || type == typeof(ValueTask)) return true;
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            var result = type.GetGenericArguments()[0];
            return !typeof(Task).IsAssignableFrom(result) && result != typeof(ValueTask)
                && !(result.IsGenericType && result.GetGenericTypeDefinition() == typeof(ValueTask<>)) && SupportedResult(result);
        }
        if (type.IsArray) return SupportedResult(type.GetElementType()!)
            && !typeof(Task).IsAssignableFrom(type.GetElementType()!);
        // Deferred enumerables, callbacks and resources require a separately defined lifetime contract.
        return !type.IsByRef && !type.IsByRefLike && !type.IsPointer && !type.IsInterface && type != typeof(object)
            && !typeof(Delegate).IsAssignableFrom(type) && !typeof(IDisposable).IsAssignableFrom(type)
            && !typeof(IAsyncDisposable).IsAssignableFrom(type)
            && !typeof(Task).IsAssignableFrom(type)
            && (type == typeof(string) || type.IsArray || !typeof(System.Collections.IEnumerable).IsAssignableFrom(type));
    }
    internal Func<MethodInfo, object?[]?, object?> Dispatch { private get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => Dispatch(targetMethod ?? throw new InvalidOperationException("plugin_contribution_method_missing"), args);

    internal static object? Complete(Type type, object? result, IDisposable lease)
    {
        if (type == typeof(Task)) return Await((Task)result!, lease);
        if (type == typeof(ValueTask)) return new ValueTask(Await(((ValueTask)result!).AsTask(), lease));
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>)
            || type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            var name = type.GetGenericTypeDefinition() == typeof(Task<>) ? nameof(AwaitResult) : nameof(AwaitValue);
            return typeof(PluginContributionProxy).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type.GetGenericArguments()).Invoke(null, [result, lease]);
        }
        lease.Dispose();
        return result;
    }
    private static async Task Await(Task task, IDisposable lease)
    { using (lease) await task.ConfigureAwait(false); }
    private static async Task<T> AwaitResult<T>(Task<T> task, IDisposable lease)
    { using (lease) return await task.ConfigureAwait(false); }
    private static async ValueTask<T> AwaitValue<T>(ValueTask<T> task, IDisposable lease)
    { using (lease) return await task.ConfigureAwait(false); }
}
