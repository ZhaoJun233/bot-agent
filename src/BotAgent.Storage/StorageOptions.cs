namespace BotAgent.Storage;

public sealed class StorageOptions
{
    public string DbPath { get; set; } = "botagent.db";
    public int BackupIntervalHours { get; set; } = 24;
    public int MaxRetainedBackups { get; set; } = 3;
}
