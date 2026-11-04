using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace BotAgent.Panel;

internal sealed class ZbaBadRequestException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal sealed class ZbaBodyTooLargeException : Exception
{
}

internal static class ZbaJson
{
    internal static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 契约按精确大小写绑定：否则 kind/Kind 会落到同一成员，绕过 ordinal 重复字段检查。
        PropertyNameCaseInsensitive = false,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    internal static T Parse<T>(byte[] body, int maxBytes)
    {
        if (body.Length == 0) throw new ZbaBadRequestException("empty_body");
        if (body.Length > maxBytes) throw new ZbaBodyTooLargeException();

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            throw new ZbaBadRequestException("invalid_utf8");
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            Validate(document.RootElement);
            return JsonSerializer.Deserialize<T>(text, Wire)
                ?? throw new ZbaBadRequestException("invalid_json");
        }
        catch (JsonException)
        {
            throw new ZbaBadRequestException("invalid_json");
        }
        catch (InvalidOperationException)
        {
            throw new ZbaBadRequestException("invalid_json");
        }
    }

    internal static JsonDocument ParseDocument(byte[] body, int maxBytes)
    {
        if (body.Length == 0) throw new ZbaBadRequestException("empty_body");
        if (body.Length > maxBytes) throw new ZbaBodyTooLargeException();
        try
        {
            var text = new UTF8Encoding(false, true).GetString(body);
            var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            Validate(document.RootElement);
            return document;
        }
        catch (DecoderFallbackException)
        {
            throw new ZbaBadRequestException("invalid_utf8");
        }
        catch (JsonException)
        {
            throw new ZbaBadRequestException("invalid_json");
        }
    }

    internal static byte[] Serialize<T>(T value)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Wire));

    private static void Validate(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new ZbaBadRequestException("duplicate_field");
                    Validate(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Validate(item);
                break;
            case JsonValueKind.String:
                ValidateUnicode(element.GetString() ?? string.Empty);
                break;
        }
    }

    private static void ValidateUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    throw new ZbaBadRequestException("invalid_unicode");
                index++;
            }
            else if (char.IsLowSurrogate(current))
            {
                throw new ZbaBadRequestException("invalid_unicode");
            }
        }
    }
}
