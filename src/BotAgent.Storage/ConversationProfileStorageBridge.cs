using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using Microsoft.Data.Sqlite;

namespace BotAgent.Storage;

/// <summary>
/// 会话级可组合档案的事务存储桥：经 <c>meta['configuration_conversation_profiles']</c> 持久化，
/// 0 新增 SQL 字面量、不写入 <c>configuration_field_modes</c>、保持 SQLite Schema v15 零迁移。
/// </summary>
internal static class ConversationProfileStorageBridge
{
    private const string MetaKey = "configuration_conversation_profiles";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions Codec = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static ImmutableArray<ConversationProfile> Load(SqliteConnection connection)
    {
        var json = AppDatabase.GetMetaInTransaction(connection, MetaKey);
        if (json is null) return ImmutableArray<ConversationProfile>.Empty;
        try
        {
            if (StrictUtf8.GetByteCount(json) > ConversationProfileValidator.MaxJsonBytes)
                throw new JsonException("conversation_profile_payload_too_large");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var seenProps = 0;
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name is not ("version" or "entries")) throw new JsonException();
                seenProps++;
            }
            if (seenProps != 2 || !root.GetProperty("version").TryGetInt32(out var version) || version != 1)
                throw new JsonException();
            var rows = root.GetProperty("entries");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > ConversationProfileValidator.MaxProfiles)
                throw new JsonException();
            var list = ImmutableArray.CreateBuilder<ConversationProfile>();
            var seen = new HashSet<ConversationProfileKey>();
            foreach (var row in rows.EnumerateArray())
            {
                var item = row.Deserialize<ConversationProfile>(Codec) ?? throw new JsonException();
                var frozen = ConversationProfileValidator.FreezeProfile(item);
                if (!seen.Add(frozen.Key)) throw new JsonException();
                list.Add(frozen);
            }
            return list.OrderBy(item => ConversationProfileValidator.KeyOrder(item.Key), StringComparer.Ordinal)
                .ToImmutableArray();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException
            or NotSupportedException or OverflowException)
        {
            throw new JsonException("invalid_conversation_profile_state");
        }
    }

    internal static void Write(SqliteConnection connection, ImmutableArray<ConversationProfile> profiles)
    {
        if (profiles.IsDefault || profiles.Length > ConversationProfileValidator.MaxProfiles)
            throw new JsonException("invalid_conversation_profile_state");
        var json = SerializeEnvelope(profiles);
        if (StrictUtf8.GetByteCount(json) > ConversationProfileValidator.MaxJsonBytes)
            throw new JsonException("conversation_profile_payload_too_large");
        AppDatabase.SetMetaInTransaction(connection, MetaKey, json);
    }

    internal static string SerializeEnvelope(ImmutableArray<ConversationProfile> profiles)
    {
        ImmutableArray<ConversationProfile> ordered;
        try
        {
            ordered = profiles.Select(ConversationProfileValidator.FreezeProfile)
                .OrderBy(item => ConversationProfileValidator.KeyOrder(item.Key), StringComparer.Ordinal)
                .ToImmutableArray();
        }
        catch (ArgumentException)
        {
            throw new JsonException("invalid_conversation_profile_state");
        }
        if (ordered.Select(item => item.Key).Distinct().Count() != ordered.Length)
            throw new JsonException("invalid_conversation_profile_state");
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("entries");
            foreach (var entry in ordered)
                JsonSerializer.Serialize(writer, entry, Codec);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return StrictUtf8.GetString(output.ToArray());
    }
}
