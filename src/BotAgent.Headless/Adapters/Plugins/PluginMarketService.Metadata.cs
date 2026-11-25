using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginMarketService
{
    private static IReadOnlyList<PluginPackageMetadataFailure> MetadataFailures(IReadOnlyList<InstalledPluginPackage> installed)
        => installed.Where(p => p.MetadataFailure is not null).Select(p => p.MetadataFailure!).ToArray();

    private static void EnsureMetadataReadable(InstalledPluginPackage? package)
    {
        if (package?.MetadataFailure is not null)
            throw new ZbaBackendException(409, "plugin_metadata_unreadable", "插件安装记录无法读取；请先恢复兼容的记录，原文件保持不变。");
    }
}
