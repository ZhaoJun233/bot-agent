using System.Diagnostics;
using System.Security;
using BotAgent.Domain.Plugins;

internal static class SharedContractFixture
{
    internal sealed record Outputs(string Contract, string ContractV2, string ProviderPrivate, string ConsumerPrivate,
        string Provider, string Consumer, string ConsumerV2, string ReaderOne);

    internal static async Task<Outputs> BuildAsync(string root)
    {
        const string contract = "public interface IUsageSnapshot { int Read(); string Origin { get; } }";
        var independent = await Task.WhenAll(
            Build(root, "contract1", "Synthetic.Usage.Contracts", "1.0.0.0", contract),
            Build(root, "contract2", "Synthetic.Usage.Contracts", "2.0.0.0", contract),
            Build(root, "private1", "Synthetic.Private", "1.0.0.0", "public static class PrivateValue { public static string Value => \"provider-1\"; }"),
            Build(root, "private2", "Synthetic.Private", "2.0.0.0", "public static class PrivateValue { public static string Value => \"consumer-2\"; }"),
            Build(root, "reader1", "Synthetic.Reader", "1.0.0.0",
                "[assembly: System.Reflection.AssemblyMetadata(\"BotAgent.PluginStateReaderVersion\", \"1\")]"));
        var core = typeof(IBotPlugin).Assembly.Location;
        var plugins = await Task.WhenAll(
            Build(root, "provider", "Provider", "1.0.0.0", ProviderSource, core, independent[0], independent[2]),
            Build(root, "consumer", "Consumer", "1.0.0.0", ConsumerSource, core, independent[0], independent[3]),
            Build(root, "consumer2", "Consumer", "1.0.0.0", ConsumerSource, core, independent[1], independent[3]));
        return new(independent[0], independent[1], independent[2], independent[3], plugins[0], plugins[1], plugins[2], independent[4]);
    }

    private static async Task<string> Build(string root, string folder, string name, string version, string source, params string[] references)
    {
        var directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "Fixture.csproj");
        var refs = string.Join("", references.Select(path => $"<Reference Include=\"{Path.GetFileNameWithoutExtension(path)}\"><HintPath>{SecurityElement.Escape(path)}</HintPath></Reference>"));
        await File.WriteAllTextAsync(project, $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>
            <AssemblyName>{name}</AssemblyName><AssemblyVersion>{version}</AssemblyVersion>
            <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><Deterministic>true</Deterministic>
            </PropertyGroup><ItemGroup>{refs}</ItemGroup></Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(directory, "Plugin.cs"), source);
        // No packages or workspace project references: each binary compiles in its own clean directory.
        await File.WriteAllTextAsync(Path.Combine(directory, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "build", project, "-c", "Release", "--nologo", "--verbosity", "quiet" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        var output = await stdout + await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Independent fixture build failed: " + folder + Environment.NewLine + output);
        return Path.Combine(directory, "bin", "Release", "net10.0", name + ".dll");
    }

    private const string ProviderSource = """
        using BotAgent.Domain.Plugins;
        public sealed class Provider : IBotPlugin, IUsageSnapshot
        {
            public string Id => "test.contract.provider";
            public string Name => "Synthetic usage producer";
            public string Version => "1.0.0";
            public string Description => "Synthetic contract fixture";
            public PluginCategory Category => PluginCategory.Feature;
            private Func<int> read = () => 0;
            private Action shutdown = () => {};
            public string Origin => PrivateValue.Value;
            public int Read() => read();
            public Task InitializeAsync(PluginContext context, CancellationToken ct)
            {
                read = context.Services.GetService(typeof(Func<int>)) as Func<int> ?? (() => 17);
                shutdown = context.Services.GetService(typeof(Action)) as Action ?? (() => {});
                (context.Services.GetService(typeof(Action<object>)) as Action<object>)?.Invoke(this);
                return Task.CompletedTask;
            }
            public Task ShutdownAsync(CancellationToken ct) { shutdown(); return Task.CompletedTask; }
        }
        """;

    private const string ConsumerSource = """
        using BotAgent.Domain.Plugins;
        public class Consumer : IReplyPolicyPlugin
        {
            public virtual string Id => "test.contract.consumer";
            public string Name => "Synthetic quota consumer";
            public string Version => "1.0.0";
            public string Description => "Synthetic contract fixture";
            public PluginCategory Category => PluginCategory.Feature;
            private IUsageSnapshot usage = null!;
            public Task InitializeAsync(PluginContext context, CancellationToken ct)
            {
                usage = (IUsageSnapshot)((Func<object>)context.Services.GetService(typeof(Func<object>))!)();
                return Task.CompletedTask;
            }
            public string? BlockReason(ReplyPolicyContext context) => $"usage:{usage.Read()}:{usage.Origin}:{PrivateValue.Value}";
            public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
        }
        public sealed class ChangedConsumer : Consumer { public override string Id => "test.contract.changed"; }
        public sealed class UndeclaredConsumer : Consumer { public override string Id => "test.contract.undeclared"; }
        public sealed class ReferenceConsumer : Consumer { public override string Id => "test.contract.reference"; }
        """;
}
