using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Plugins;

namespace BotAgent.Panel;

/// <summary>Host composition only. A request or an installed manifest cannot populate this catalog.</summary>
public interface IZbaHttpContributions
{
    IReadOnlyList<PluginHttpBinding<ConfigurationCaller>> HttpContributions { get; }
}

internal sealed class ZbaHttpCatalog
{
    private readonly Dictionary<string, PluginHttpBinding<ConfigurationCaller>> _handlers = new(StringComparer.Ordinal);
    private readonly JsonObject _schemas = new();
    internal IReadOnlyList<ZbaRouteInfo> Routes { get; }
    internal string[] Features => _handlers.Keys.ToArray();

    internal ZbaHttpCatalog(IEnumerable<PluginHttpBinding<ConfigurationCaller>> bindings, IEnumerable<string> reservedFeatures)
    {
        var routes = ZbaRoutes.All.ToList();
        var owners = new HashSet<string>(StringComparer.Ordinal);
        var operations = routes.Select(r => r.OperationId).Concat(reservedFeatures).ToHashSet(StringComparer.Ordinal);
        var schemaNames = ZbaOpenApi.BaseSchemas().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (binding is null || binding.Description is null) throw Invalid();
            var description = binding.Description;
            if (binding.Invoke is null || !Token(description.Owner, 80) || !owners.Add(description.Owner)
                || owners.Count > 128 || description.Routes is null || description.Routes.Count is 0 or > 32
                || description.SchemasJson is null || description.SchemasJson.Length > 262144)
                throw Invalid();
            JsonObject schemas;
            try
            {
                using var document = ZbaJson.ParseDocument(System.Text.Encoding.UTF8.GetBytes(description.SchemasJson), 262144);
                schemas = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject ?? throw Invalid();
            }
            catch (Exception error) when (error is JsonException or ZbaBadRequestException or ZbaBodyTooLargeException)
            { throw Invalid(); }
            if (schemas.Count > 64) throw Invalid();
            foreach (var (name, schema) in schemas)
            {
                if (!Token(name, 120) || schema is not JsonObject || !schemaNames.Add(name)) throw Invalid();
                _schemas.Add(name, schema.DeepClone());
            }
            foreach (var route in description.Routes)
            {
                if (route is null || route.Method is not ("GET" or "POST" or "PUT" or "DELETE" or "PATCH")
                    || !ValidPath(route.Path) || !Token(route.OperationId, 120) || !operations.Add(route.OperationId)
                    || string.IsNullOrWhiteSpace(route.Summary) || route.Summary.Length > 500 || route.Summary.Any(char.IsControl)
                    || !Token(route.ResponseSchema, 120) || (route.RequestSchema is not null && !Token(route.RequestSchema, 120))
                    || (route.Method == "GET" && route.RequestSchema is not null)
                    || ZbaRoutes.HasPath(route.Path)
                    || (paths.TryGetValue(route.Path, out var owner) && owner != description.Owner)
                    || routes.Any(r => r.Method == route.Method && r.Template == route.Path)) throw Invalid();
                paths[route.Path] = description.Owner;
                routes.Add(new(route.Method, route.Path, route.Admin ? ZbaScope.Admin : ZbaScope.Read, false,
                    route.OperationId, route.Summary, route.RequestSchema, route.ResponseSchema));
                _handlers.Add(route.OperationId, binding);
            }
        }
        foreach (var route in routes)
            if (!schemaNames.Contains(route.ResponseSchema) || route.RequestSchema is { } request && !schemaNames.Contains(request)) throw Invalid();
        ValidateReferences(_schemas, schemaNames);
        Routes = new ReadOnlyCollection<ZbaRouteInfo>(routes);
    }

    internal JsonObject Schemas() => (JsonObject)_schemas.DeepClone();
    internal bool Contains(string operation) => _handlers.ContainsKey(operation);
    internal ValueTask<object> Invoke(string operation, ConfigurationCaller caller, PluginHttpBody body, CancellationToken ct)
        => _handlers[operation].Invoke(operation, caller, body, ct);

    private static void ValidateReferences(JsonNode? node, HashSet<string> names)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                if (key == "$ref" && (value is not JsonValue text || !text.TryGetValue<string>(out var reference)
                    || !reference.StartsWith("#/components/schemas/", StringComparison.Ordinal)
                    || !names.Contains(reference[21..]))) throw Invalid();
                ValidateReferences(value, names);
            }
        }
        else if (node is JsonArray array) foreach (var item in array) ValidateReferences(item, names);
    }

    private static bool Token(string? text, int max) => text is { Length: > 0 } && text.Length <= max
        && char.IsAsciiLetter(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool ValidPath(string? path) => path is { Length: <= 512 }
        && path.StartsWith("/zba/v1/", StringComparison.Ordinal) && path[8..].Split('/').All(segment => Token(segment, 120)
            && !segment.Contains("..", StringComparison.Ordinal));
    private static InvalidOperationException Invalid() => new("zba_contribution_invalid");
}
