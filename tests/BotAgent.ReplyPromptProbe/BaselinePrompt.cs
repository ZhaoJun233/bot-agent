using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Services.Model;

internal sealed class BaselinePrompt : IDisposable
{
    private readonly AssemblyLoadContext _context = new("paced-prompt-baseline", isCollectible: true);
    private readonly Type _requestType;
    private readonly MethodInfo _build;
    private readonly MethodInfo _parts;

    public BaselinePrompt(string assemblyPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        _context.Resolving += (context, name) =>
        {
            var file = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
        };
        var model = _context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        var builder = model.GetType("BotAgent.Services.Model.PromptBuilder", throwOnError: true)!;
        _requestType = builder.GetNestedType("PromptRequest")!;
        _build = builder.GetMethod("Build")!;
        _parts = builder.GetMethod("BuildParts")!;
        var clock = model.GetType("BotAgent.Model.ModelClock", throwOnError: true)!.GetMethod("Initialize")!;
        var proxy = DispatchProxy.Create(clock.GetParameters()[0].ParameterType, typeof(BaselineClockProxy));
        clock.Invoke(null, [proxy]);
    }

    public (string Stable, string Text) Build(PromptBuilder.PromptRequest request, int desire, bool pacing)
    {
        var node = JsonSerializer.SerializeToNode(request)!.AsObject();
        node["AiDesire"] = desire;
        node["ReplyPacingEnabled"] = pacing;
        var baselineRequest = node.Deserialize(_requestType)!;
        var parts = _parts.Invoke(null, [baselineRequest])!;
        return ((string)parts.GetType().GetProperty("Stable")!.GetValue(parts)!,
            (string)_build.Invoke(null, [baselineRequest])!);
    }

    public void Dispose() => _context.Unload();
}

public class BaselineClockProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
    {
        "get_Now" or "get_UtcNow" => new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
        "get_LocalDateTime" => new DateTime(2030, 1, 2, 3, 4, 5),
        "get_TickCount" => 0L,
        _ => throw new InvalidOperationException("Unexpected baseline clock operation")
    };
}
