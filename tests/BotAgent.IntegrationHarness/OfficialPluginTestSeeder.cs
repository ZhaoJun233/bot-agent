using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace BotAgent.IntegrationHarness;

internal static class OfficialPluginTestSeeder
{
    private static readonly string[] FeatureIds =
    [
        "preset.feature.digest",
        "preset.feature.documents",
        "preset.feature.knowledge",
        "preset.feature.music",
        "preset.feature.personal-todo",
        "preset.feature.poke",
        "preset.feature.profiles",
        "preset.feature.scheduler",
        "preset.feature.stickers",
        "preset.feature.token-quota",
        "preset.feature.vibes",
        "preset.feature.voice",
        "preset.feature.websearch",
    ];

    internal static void SeedIfNeeded(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var releasesRoot = ResolveReleasesRoot();
        var dataSub = Path.Combine(runtimeRoot, "data");
        var pluginsDir = Path.Combine(dataSub, "plugins");
        Directory.CreateDirectory(pluginsDir);

        foreach (var id in FeatureIds)
        {
            var packageDir = Path.Combine(pluginsDir, id);
            var installedPath = Path.Combine(packageDir, "installed.json");
            if (File.Exists(installedPath)) continue;

            var sourceDir = Path.Combine(releasesRoot, id);
            if (!Directory.Exists(sourceDir))
                sourceDir = Path.Combine(releasesRoot, id + "-1.1.0");
            var manifestPath = Path.Combine(sourceDir, "manifest.json");
            var zipPath = Path.Combine(sourceDir, id + "-1.1.0.zip");
            if (!File.Exists(manifestPath) || !File.Exists(zipPath))
                throw new FileNotFoundException($"Missing official marketplace artifact for {id} under {sourceDir}.");

            var manifestNode = JsonNode.Parse(File.ReadAllBytes(manifestPath))?.AsObject()
                ?? throw new InvalidDataException($"Invalid official manifest: {manifestPath}");
            var version = (manifestNode["version"] ?? manifestNode["Version"])?.GetValue<string>() ?? "1.1.0";
            var shaLower = ((manifestNode["sha256"] ?? manifestNode["Sha256"])?.GetValue<string>()
                ?? throw new InvalidDataException($"Missing sha256 in {manifestPath}")).ToLowerInvariant();

            var releasesDir = Path.Combine(packageDir, "releases");
            Directory.CreateDirectory(releasesDir);
            File.Copy(zipPath, Path.Combine(releasesDir, $"{version}-{shaLower}.zip"), overwrite: true);

            var executionObj = new JsonObject
            {
                ["id"] = id,
                ["active"] = manifestNode.DeepClone(),
                ["previous"] = null,
                ["pending"] = null,
                ["pendingUninstall"] = false
            };
            var executionBytes = Encoding.UTF8.GetBytes(executionObj.ToJsonString());
            var executionSha = Convert.ToHexString(SHA256.HashData(executionBytes)).ToLowerInvariant();

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", 1);
                writer.WriteNumber("minimumReaderVersion", 12);
                writer.WriteString("id", id);
                writer.WritePropertyName("active");
                writer.WriteStartObject();
                writer.WriteString("version", version);
                writer.WriteString("sha256", shaLower);
                writer.WriteEndObject();
                writer.WriteNull("previous");
                writer.WriteNull("pending");
                writer.WriteBoolean("pendingUninstall", false);
                writer.WriteString("executionSha256", executionSha);
                writer.WritePropertyName("execution");
                writer.WriteRawValue(executionBytes);
                writer.WriteEndObject();
            }
            File.WriteAllBytes(installedPath, ms.ToArray());
        }

        // Seed default enabled state for the 13 official features if not already persisted.
        var canonicalDb = Path.Combine(dataSub, "botagent.db");
        var legacyDb = Path.Combine(dataSub, "qqchat.db");
        var dbPath = !File.Exists(canonicalDb) && File.Exists(legacyDb) ? legacyDb : canonicalDb;

        var statesJson = JsonSerializer.Serialize(FeatureIds.ToDictionary(id => id, _ => true, StringComparer.Ordinal));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT OR IGNORE INTO meta(key, value) VALUES('plugins_state', $states);
            """;
        cmd.Parameters.AddWithValue("$states", statesJson);
        cmd.ExecuteNonQuery();
    }

    private static string ResolveReleasesRoot()
    {
        var configured = Environment.GetEnvironmentVariable("BOTAGENT_OFFICIAL_PLUGINS_DIR")?.Trim();
        string marketplaceRoot;
        if (!string.IsNullOrEmpty(configured))
        {
            marketplaceRoot = Path.GetFullPath(configured);
        }
        else
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BotAgent.slnx")))
                dir = dir.Parent;
            if (dir is null)
                throw new DirectoryNotFoundException("BotAgent.slnx not found while locating bot-agent-plugins.");
            marketplaceRoot = Path.GetFullPath(Path.Combine(dir.FullName, "..", "bot-agent-plugins"));
        }
        var releasesRoot = Path.Combine(marketplaceRoot, "artifacts", "official-releases");
        if (!Directory.Exists(releasesRoot))
            throw new DirectoryNotFoundException("Official plugin releases directory not found: " + releasesRoot);
        return releasesRoot;
    }
}
