using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Tools;

namespace BotAgent.Services.Tools;

/// <summary>A host-owned invocation handle, fixed to one adopted provider generation.</summary>
public interface IToolBinding
{
    string ProviderId { get; }
    string Generation { get; }
    ToolDefinition Definition { get; }
    bool IsReady { get; }
    Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct = default);
    /// <summary>Auxiliary contribution from this exact generation, with its own invocation lease.</summary>
    T GetContribution<T>() where T : class, IPluginContribution;
}

/// <summary>One adopted declaration set shared by policy, prompts and execution.</summary>
public sealed class ChatToolCatalog
{
    public static ChatToolCatalog Builtin { get; } = new([]);
    public IReadOnlyList<ToolSpec> Specs { get; }
    public IReadOnlyDictionary<string, IToolBinding> Bindings { get; }
    public string Fingerprint { get; }

    public ChatToolCatalog(IEnumerable<IToolBinding> bindings)
    {
        var captured = bindings.OrderBy(binding => binding.Definition.Spec.Id, StringComparer.Ordinal).ToArray();
        Bindings = captured.ToFrozenDictionary(binding => binding.Definition.Spec.Id, StringComparer.Ordinal);
        Specs = Array.AsReadOnly(ChatToolSpecs.All.Concat(captured.Select(binding => binding.Definition.Spec)).ToArray());
        if (Specs.Select(spec => spec.Id).Distinct(StringComparer.Ordinal).Count() != Specs.Count)
            throw new InvalidOperationException("plugin_tool_conflict");
        Fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Specs,
            Providers = captured.Select(binding => new { binding.ProviderId, binding.Generation, binding.Definition })
        })));
    }

    public IToolBinding? FindBinding(string id) => Bindings.GetValueOrDefault(id);
    public ToolSpec? Find(string id) => Specs.FirstOrDefault(spec => spec.Id == id);
}
