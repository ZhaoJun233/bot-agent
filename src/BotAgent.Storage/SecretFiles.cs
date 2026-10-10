using System.IO;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 从文件读密钥（Docker secrets 的 <c>{NAME}_FILE</c> 约定）。
/// 密钥本身仍然只从环境变量 / 库里读 —— 这里只是"把那个文件读出来"这一步的实现细节；
/// 读不到就返回 null，由调用方回落到环境变量（可选文件不该让启动挂掉）。
/// </summary>
public static class SecretFiles
{
    /// <summary>读一个密钥文件并去掉首尾空白；文件不在/读不了 → null，<paramref name="error" /> 给日志用。</summary>
    public static string? TryRead(string path, out string? error)
    {
        error = null;
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
