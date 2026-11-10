using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

internal static class ProbeFixture
{
    // Deterministic synthetic inputs committed here; no downloads, pip, or private data.
    internal static string[] Prepare(string tempRoot, string executable)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BotAgent.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Run inside the source checkout.");
        var fixtures = Path.Combine(root.FullName, "tests", "BotAgent.PythonMarketProbe", "Fixtures");
        var fixturePath = Path.Combine(fixtures, "python-contract.json");
        var manifest = JsonNode.Parse(File.ReadAllText(fixturePath))!["manifest"]!.DeepClone();
        var artifact = Path.Combine(tempRoot, "artifact");
        Directory.CreateDirectory(artifact);
        var zipPath = Path.Combine(artifact, manifest["Id"]!.GetValue<string>() + "-1.0.0.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            Add(archive, "worker.py", Path.Combine(fixtures, "worker.py"));
            Add(archive, "botagent_worker.py", Path.Combine(root.FullName, "src", "BotAgent.Mcp", "PythonWorkerSdk", "botagent_worker.py"));
        }
        manifest["Sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(artifact, "manifest.json"), manifest.ToJsonString());
        return new[] { fixturePath, artifact, executable };
    }
    private static void Add(ZipArchive archive, string name, string source)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var output = entry.Open();
        using var input = File.OpenRead(source);
        input.CopyTo(output);
    }
}
