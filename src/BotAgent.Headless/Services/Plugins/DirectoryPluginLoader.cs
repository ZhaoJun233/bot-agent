using System.Reflection;
using System.Runtime.Loader;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;

namespace BotAgent.Services.Plugins;

/// <summary>
/// 目录程序集插件加载器：从指定目录隔离加载外部 DLL 插件，实现核心与插件二进制解耦。
/// </summary>
public sealed class DirectoryPluginLoader : IPluginLoader
{
    private readonly string _pluginsDirectory;
    private readonly IDiagnosticEventBus? _diagnostics;
    private readonly IClock _clock;
    private readonly List<IBotPlugin> _embeddedPlugins = new();

    public DirectoryPluginLoader(string pluginsDirectory, IDiagnosticEventBus? diagnostics, IClock clock)
    {
        _pluginsDirectory = pluginsDirectory;
        _diagnostics = diagnostics;
        _clock = clock;
    }

    /// <summary>登记预置/桥接插件，统一由加载器接管生命周期。</summary>
    public void RegisterPlugin(IBotPlugin plugin)
    {
        _embeddedPlugins.Add(plugin);
    }

    public async Task<IReadOnlyList<IBotPlugin>> LoadPluginsAsync(IPluginContext context, CancellationToken ct)
    {
        var loaded = new List<IBotPlugin>(_embeddedPlugins);

        if (Directory.Exists(_pluginsDirectory))
        {
            var dlls = Directory.GetFiles(_pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly);
            foreach (var dll in dlls)
            {
                var fileName = Path.GetFileName(dll);
                if (fileName.Equals("BotAgent.Headless.dll", StringComparison.OrdinalIgnoreCase)) continue;
                PluginLoadContext? alc = null;
                try
                {
                    alc = new PluginLoadContext(dll);
                    var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(dll));
                    var pluginTypes = asm.GetTypes()
                        .Where(t => typeof(IBotPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

                    foreach (var type in pluginTypes)
                    {
                        try
                        {
                            if (Activator.CreateInstance(type) is IBotPlugin instance)
                            {
                                loaded.Add(instance);
                                context.Log($"[+] 发现插件: {instance.Name} ({instance.Id}) v{instance.Version} [状态: Loaded]");
                            }
                        }
                        catch (Exception ex)
                        {
                            var report = PluginDiagnosticAnalyzer.Analyze(type.FullName ?? "Unknown", fileName, ex, PluginLifecycleState.Quarantined, _clock.UtcNow);
                            _diagnostics?.Publish(report);
                            context.Log($"[-] 插件 {type.Name} 实例化失败，已安全跳过并隔离。故障码: {report.ErrorCode} ({report.RootCause.Message})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    var report = PluginDiagnosticAnalyzer.Analyze("Assembly." + fileName, fileName, ex, PluginLifecycleState.Quarantined, _clock.UtcNow);
                    _diagnostics?.Publish(report);
                    context.Log($"[-] 程序集 {fileName} 加载失败，已安全跳过并隔离。故障码: {report.ErrorCode} ({report.RootCause.Message})");
                    try { alc?.Unload(); } catch { }
                }
            }
        }

        var active = new List<IBotPlugin>();
        foreach (var plugin in loaded)
        {
            try
            {
                await plugin.InitializeAsync(context, ct);
                active.Add(plugin);
            }
            catch (Exception ex)
            {
                var report = PluginDiagnosticAnalyzer.Analyze(plugin.Id, plugin.GetType().Assembly.GetName().Name ?? "Unknown", ex, PluginLifecycleState.Quarantined, _clock.UtcNow);
                _diagnostics?.Publish(report);
                context.Log($"[-] 插件 {plugin.Name} 初始化异常，已安全隔离并跳过。故障码: {report.ErrorCode} ({report.RootCause.Message})");
            }
        }

        return active;
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private static readonly string[] SharedPrefixes = { "System.", "Microsoft.", "BotAgent.Domain", "BotAgent.Headless" };

        public PluginLoadContext(string pluginPath) : base(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = assemblyName.Name ?? string.Empty;
            if (SharedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                name == typeof(IBotPlugin).Assembly.GetName().Name)
            {
                try
                {
                    return Default.LoadFromAssemblyName(assemblyName);
                }
                catch
                {
                    // 若默认上下文无法加载则回落
                }
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }
    }
}