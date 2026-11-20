using System.Collections.Immutable;
using System.Text.Json.Nodes;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Tools;

namespace BotAgent.Services.Tools;

/// <summary>Materialized tool metadata and execution from one initialized plugin instance.</summary>
public interface IToolContribution : IPluginContribution
{
    public const int CurrentApiVersion = 1;
    ToolDefinition[] DescribeTools();
    Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct);
}

/// <summary>Only immutable public values may be retained after the owner is unloaded.</summary>
public sealed record ToolDefinition(ToolSpec Spec, ImmutableArray<ArgField> Fields, string? HttpUrlField = null);

/// <summary>Argument checks use the definition captured alongside its executor.</summary>
public static class ToolArguments
{
    public static ArgsVerdict Validate(ToolDefinition definition, JsonObject? arguments)
    {
        arguments ??= new JsonObject();
        foreach (var pair in arguments)
            if (!definition.Fields.Any(field => field.Name == pair.Key))
                return ArgsVerdict.Fail("unknown_field", pair.Key);
        foreach (var field in definition.Fields)
        {
            var node = arguments[field.Name];
            if (node is not null && (node is not JsonValue value || !value.TryGetValue<string>(out _)))
                return ArgsVerdict.Fail("bad_type", field.Name);
            var text = node?.GetValue<string>()?.Trim() ?? string.Empty;
            if (field.Required && text.Length == 0) return ArgsVerdict.Fail("missing_required", field.Name);
            if (text.Length == 0) continue;
            if (field.MinLength > 0 && text.Length < field.MinLength)
                return ArgsVerdict.Fail("bad_length", $"{field.Name}<{field.MinLength}");
            if (field.MaxLength > 0 && text.Length > field.MaxLength)
                return ArgsVerdict.Fail("bad_length", $"{field.Name}>{field.MaxLength}");
        }
        if (definition.HttpUrlField is { } urlField)
        {
            var url = arguments[urlField]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return ArgsVerdict.Fail("bad_scheme", urlField);
        }
        return ArgsVerdict.Pass;
    }
}
