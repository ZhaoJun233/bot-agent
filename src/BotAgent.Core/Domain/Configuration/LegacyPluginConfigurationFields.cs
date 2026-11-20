using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using BotAgent.Domain.Plugins;
using BotAgent.Services;

namespace BotAgent.Domain.Configuration;

/// <summary>Compatibility identity only; schema, validation and interpretation belong to the package.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LegacyPluginConfigurationFieldAttribute(string owner, string key, int legacyFieldId = -1) : Attribute
{
    public string Owner { get; } = owner;
    public string Key { get; } = key;
    public int? LegacyFieldId { get; } = legacyFieldId < 0 ? null : legacyFieldId;
}

/// <summary>Closed compatibility mappings. No arbitrary property lookup from package input.</summary>
public static class LegacyPluginConfigurationFields
{
    private sealed record Field(PropertyInfo Property, LegacyPluginConfigurationFieldAttribute Identity);
    private static readonly IReadOnlyDictionary<(string Owner, string Key), Field> Fields = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(property => (Property: property, Identity: property.GetCustomAttribute<LegacyPluginConfigurationFieldAttribute>()))
        .Where(value => value.Identity is not null)
        .ToDictionary(value => (value.Identity!.Owner, value.Identity.Key), value => new Field(value.Property, value.Identity!));

    public static void Validate(PluginPackageManifest manifest)
    {
        if (manifest.Configuration is not { } declaration) return;
        if (manifest.ApiVersion != 1 || declaration.ApiVersion != 1 || declaration.Bindings is not { Count: > 0 and <= 128 })
            throw Invalid();
        Dictionary<string, PluginSettingDefinition> schema;
        try { schema = PluginSettingValidation.ReadSchema(manifest.Settings).ToDictionary(field => field.Key, StringComparer.Ordinal); }
        catch (PluginSettingsException) { throw Invalid(); }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in declaration.Bindings)
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.Key) || !keys.Add(binding.Key)) throw Invalid();
            var field = Resolve(manifest.Id, binding);
            if (binding.Trim && (binding.ReadOnly || field.Property.PropertyType != typeof(string))) throw Invalid();
            if (binding.ReadOnly)
            {
                if (binding.LegacyFieldId is not null || schema.ContainsKey(binding.Key)) throw Invalid();
            }
            else
            {
                if (!schema.TryGetValue(binding.Key, out var definition) || definition.Type != TypeName(field.Property.PropertyType)
                    || definition.Type == "secret") throw Invalid();
                schema.Remove(binding.Key);
            }
        }
        if (schema.Count != 0) throw Invalid();
    }

    public static IReadOnlyDictionary<string, JsonElement> Read(string owner,
        PluginConfigurationDeclaration declaration, AppSettings settings, bool includeReadOnly = true)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var binding in declaration.Bindings.Where(binding => includeReadOnly || !binding.ReadOnly))
        {
            var property = Resolve(owner, binding).Property;
            result.Add(binding.Key, JsonSerializer.SerializeToElement(property.GetValue(settings), property.PropertyType));
        }
        return new ReadOnlyDictionary<string, JsonElement>(result);
    }

    public static ConfigurationPatch ToPatch(string owner, PluginConfigurationDeclaration declaration,
        IReadOnlyDictionary<string, JsonElement> changes)
    {
        var behavior = new BehaviorChanges();
        foreach (var binding in declaration.Bindings.Where(binding => !binding.ReadOnly && changes.ContainsKey(binding.Key)))
        {
            var field = Resolve(owner, binding);
            var property = typeof(BehaviorChanges).GetProperty(field.Property.Name) ?? throw Invalid();
            var value = changes[binding.Key];
            var editType = typeof(FieldEdit<>).MakeGenericType(field.Property.PropertyType);
            object? edit;
            try
            {
                edit = value.ValueKind == JsonValueKind.Null
                    ? editType.GetMethod(nameof(FieldEdit<int>.Clear))!.Invoke(null, null)
                    : editType.GetMethod(nameof(FieldEdit<int>.Set))!.Invoke(null,
                        [binding.Trim && value.ValueKind == JsonValueKind.String
                            ? value.GetString()!.Trim() : value.Deserialize(field.Property.PropertyType)]);
            }
            catch (Exception error) when (error is JsonException or TargetInvocationException)
            { throw new PluginSettingsException(PluginSettingsContract.InvalidValues); }
            property.SetValue(behavior, edit);
        }
        // The existing mode writer consumes a complete patch. Unbound sections are Keep.
        return new(behavior, new(), new(), new(), new(), new(), new(), new([], [], []));
    }

    public static void Apply(AppSettings settings, BehaviorChanges? changes)
    {
        if (changes is null) return;
        var defaults = new AppSettings();
        foreach (var field in Fields.Values.Where(field => field.Identity.LegacyFieldId.HasValue))
        {
            var property = typeof(BehaviorChanges).GetProperty(field.Property.Name) ?? throw Invalid();
            var edit = property.GetValue(changes)!;
            var kind = (EditKind)property.PropertyType.GetProperty(nameof(FieldEdit<int>.Kind))!.GetValue(edit)!;
            if (kind == EditKind.Keep) continue;
            if (kind == EditKind.Clear) field.Property.SetValue(settings, field.Property.GetValue(defaults));
            else if (kind == EditKind.Set)
            {
                object?[] values = [null];
                if (!(bool)property.PropertyType.GetMethod(nameof(FieldEdit<int>.TryGetValue))!.Invoke(edit, values)!) throw Invalid();
                field.Property.SetValue(settings, values[0]);
            }
            else throw Invalid();
        }
    }

    public static void ApplyValues(string owner, PluginConfigurationDeclaration declaration, AppSettings settings,
        IReadOnlyDictionary<string, JsonElement> values)
        => Apply(settings, ToPatch(owner, declaration, values).Behavior);

    public static string StoredName(string owner, PluginConfigurationBinding binding)
        => Resolve(owner, binding).Property.Name;

    private static Field Resolve(string owner, PluginConfigurationBinding binding)
    {
        if (!Fields.TryGetValue((owner, binding.Key), out var field)
            || binding.LegacyFieldId != field.Identity.LegacyFieldId
            || binding.ReadOnly != !field.Identity.LegacyFieldId.HasValue) throw Invalid();
        return field;
    }
    private static string TypeName(Type type) => type == typeof(bool) ? "boolean" : type == typeof(string) ? "string"
        : type == typeof(int) ? "number" : throw Invalid();
    private static InvalidDataException Invalid() => new("plugin_configuration_invalid_declaration");
}
