using System.Text.Json;

namespace BotAgent.Adapters.Plugins;

internal static class PluginStateCompatibilityCli
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != "--check-plugin-state" || args[2] != "--candidate")
        {
            Console.Error.WriteLine("usage: --check-plugin-state <plugin-root> --candidate <host-dll>");
            return 2;
        }
        try
        {
            var report = await PluginStateCompatibility.InspectAsync(args[1], args[3]);
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return report.Compatible ? 0 : 3;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or BadImageFormatException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine("plugin_state_preflight_failed");
            return 2;
        }
    }
}
