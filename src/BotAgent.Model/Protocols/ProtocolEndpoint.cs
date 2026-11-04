using BotAgent.Domain.Model;

namespace BotAgent.Model.Protocols;

/// <summary>Only rewrites paths on the registered authority. Authorization and socket policy stay with the host.</summary>
internal static class ProtocolEndpoint
{
    internal static Uri Build(ModelProviderRoute route, string model, bool stream)
    {
        var uri = Parse(route);
        var path = uri.Path.TrimEnd('/');
        var options = route.Options ?? new();
        uri.Path = route.Protocol switch
        {
            ModelProtocol.OpenAiChat => Append(path, "/chat/completions"),
            ModelProtocol.OpenAiResponses => Append(path, "/responses"),
            ModelProtocol.AnthropicMessages => Append(path, "/messages"),
            ModelProtocol.GeminiGenerateContent => GeminiPath(path, model, stream),
            ModelProtocol.Ollama => Append(path.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path, "/api/chat"),
            ModelProtocol.AzureOpenAi => AzurePath(path, options.AzureDeployment ?? model),
            _ => throw new ModelFault("model_protocol_unsupported")
        };
        if (route.Protocol == ModelProtocol.AzureOpenAi)
            AddQuery(uri, "api-version", options.AzureApiVersion, replace: false);
        if (route.Protocol == ModelProtocol.GeminiGenerateContent && stream)
            AddQuery(uri, "alt", "sse", replace: true);
        return uri.Uri;
    }

    internal static Uri BuildDiscovery(ModelProviderRoute route, string? cursor)
    {
        if (route.Protocol == ModelProtocol.AzureOpenAi) throw new ModelFault("model_discovery_unsupported");
        var uri = Parse(route);
        var path = uri.Path.TrimEnd('/');
        var suffix = route.Protocol switch
        {
            ModelProtocol.OpenAiChat => "/chat/completions",
            ModelProtocol.OpenAiResponses => "/responses",
            ModelProtocol.AnthropicMessages => "/messages",
            ModelProtocol.Ollama => "/chat",
            ModelProtocol.GeminiGenerateContent => "",
            _ => throw new ModelFault("model_protocol_unsupported")
        };
        if (suffix.Length > 0 && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) path = path[..^suffix.Length];
        if (route.Protocol == ModelProtocol.Ollama)
        {
            uri.Path = path.EndsWith("/api/tags", StringComparison.OrdinalIgnoreCase) ? path : Append(path, "/api") + "/tags";
        }
        else
        {
            if (route.Protocol == ModelProtocol.GeminiGenerateContent)
            {
                var models = path.LastIndexOf("/models/", StringComparison.OrdinalIgnoreCase);
                if (models >= 0) path = path[..models];
                AddQuery(uri, "alt", "json", replace: true);
                AddQuery(uri, "pageSize", "100", replace: true);
                if (cursor is not null) AddQuery(uri, "pageToken", cursor, replace: true);
            }
            else if (route.Protocol == ModelProtocol.AnthropicMessages)
            {
                AddQuery(uri, "limit", "100", replace: true);
                if (cursor is not null) AddQuery(uri, "after_id", cursor, replace: true);
            }
            uri.Path = Append(path, "/models");
        }
        return uri.Uri;
    }

    private static UriBuilder Parse(ModelProviderRoute route)
    {
        if (!Uri.TryCreate(route.BaseUrl?.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ModelFault("model_route_unavailable");
        return new UriBuilder(endpoint);
    }

    private static string Append(string path, string suffix) => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? path : path + suffix;

    private static string AzurePath(string path, string deployment)
    {
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return path;
        if (path.Contains("/deployments/", StringComparison.OrdinalIgnoreCase)) return path + "/chat/completions";
        if (string.IsNullOrWhiteSpace(deployment)) throw new ModelFault("model_route_unavailable");
        return Append(path, "/openai") + "/deployments/" + Uri.EscapeDataString(deployment) + "/chat/completions";
    }

    private static string GeminiPath(string path, string model, bool stream)
    {
        var operation = stream ? ":streamGenerateContent" : ":generateContent";
        foreach (var suffix in new[] { ":generateContent", ":streamGenerateContent" })
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return path[..^suffix.Length] + operation;
        if (string.IsNullOrWhiteSpace(model)) throw new ModelFault("model_route_unavailable");
        if (model.StartsWith("models/", StringComparison.Ordinal)) model = model[7..];
        return Append(path, "/models") + "/" + Uri.EscapeDataString(model) + operation;
    }

    private static void AddQuery(UriBuilder uri, string name, string value, bool replace)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ModelFault("model_route_unavailable");
        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool IsNamed(string part) => string.Equals(Uri.UnescapeDataString(part.Split('=')[0]), name, StringComparison.OrdinalIgnoreCase);
        if (parts.Any(IsNamed) && !replace) return;
        parts.RemoveAll(IsNamed);
        parts.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
        uri.Query = string.Join("&", parts);
    }
}
