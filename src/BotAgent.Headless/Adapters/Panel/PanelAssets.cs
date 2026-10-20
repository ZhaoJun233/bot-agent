using System.Collections.Concurrent;

namespace BotAgent.Adapters.Panel;

/// <summary>
/// 旧面板静态资源的交付适配：从发布目录的 <c>wwwroot/</c> 读取，不再嵌入 Headless 程序集。
/// 资源缺失时后端照常启动，API/健康/ZBA 不依赖这些文件。
/// 这是唯一允许触碰静态资源文件的面板文件；文件名是闭集，不做路径拼接。
/// </summary>
internal static class PanelAssets
{
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static byte[]? Read(string fileName)
    {
        if (!IsSafeName(fileName)) return null;
        if (Cache.TryGetValue(fileName, out var cached)) return cached;

        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", fileName);
        try
        {
            var bytes = File.ReadAllBytes(path);
            Cache[fileName] = bytes;
            return bytes;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSafeName(string name)
    {
        if (name.Length is 0 or > 128 || name.Contains("..", StringComparison.Ordinal)) return false;
        return name.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');
    }
}
