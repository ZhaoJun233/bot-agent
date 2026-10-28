namespace BotAgent.Adapters.Plugins;

/// <summary>
/// 插件程序集文件系统探测。
///
/// 为什么单独一个类型：核心装配层（<c>Host/CompositionRoot</c>）按 R3 不得直接做文件 IO，
/// 「这个 DLL 在不在」属于适配器该干的事。R3 口径见 <c>ArchitectureProbe</c> 的
/// 「直接文件 IO 只出现在 Adapters/** 与具名例外里」。
/// </summary>
public static class PluginAssemblyProbe
{
    /// <summary>程序集文件是否存在（不存在 = 该功能未安装）。</summary>
    public static bool Exists(string path) => File.Exists(path);
}