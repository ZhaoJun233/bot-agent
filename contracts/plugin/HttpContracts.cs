namespace BotAgent.Domain.Plugins;

/// <summary>Literal authenticated routes. Registration is a trusted composition action, not a package grant.</summary>
public sealed record PluginHttpRoute(string Method, string Path, bool Admin, string OperationId,
    string Summary, string? RequestSchema, string ResponseSchema);

public sealed record PluginHttpDescription(string Owner, IReadOnlyList<PluginHttpRoute> Routes, string SchemasJson);

/// <summary>The transport supplies its existing bounded, strict JSON reader.</summary>
public abstract class PluginHttpBody
{
    public abstract T Read<T>();
}

public sealed record PluginHttpBinding<TCaller>(PluginHttpDescription Description,
    Func<string, TCaller, PluginHttpBody, CancellationToken, ValueTask<object>> Invoke);

public sealed class PluginHttpException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
