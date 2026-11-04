namespace BotAgent.Domain.Configuration;

/// <summary>Resolves a canonical environment name first, then explicit legacy names.</summary>
public static class EnvironmentAliases
{
    public static string? Get(string canonical, params string[] legacy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonical);
        var value = Environment.GetEnvironmentVariable(canonical);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        foreach (var name in legacy)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }
}
