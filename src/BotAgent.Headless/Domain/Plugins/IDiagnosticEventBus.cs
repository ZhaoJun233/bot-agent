namespace BotAgent.Domain.Plugins;

/// <summary>
/// 诊断事件总线端口：广播插件加载异常、隔离、恢复等系统级运行期诊断事件。
/// </summary>
public interface IDiagnosticEventBus
{
    void Publish(PluginDiagnosticReport report);
    IDisposable Subscribe(Action<PluginDiagnosticReport> handler);
    IReadOnlyList<PluginDiagnosticReport> GetReports();
}