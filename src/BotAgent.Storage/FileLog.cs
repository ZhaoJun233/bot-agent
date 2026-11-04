namespace BotAgent.Storage;

public static class StorageLogging
{
    public static Action<string, string> WriteHandler { get; set; } = (tag, msg) => Console.WriteLine($"[{tag}] {msg}");
    public static Action<string, string> WarnHandler { get; set; } = (tag, msg) => Console.Error.WriteLine($"[{tag}] WARN: {msg}");

    internal static void Write(string tag, string message) => WriteHandler?.Invoke(tag, message);
    internal static void Warn(string tag, string message) => WarnHandler?.Invoke(tag, message);
}

internal static class FileLog
{
    public static void Write(string tag, string message) => StorageLogging.Write(tag, message);
    public static void Warn(string tag, string message) => StorageLogging.Warn(tag, message);
}
