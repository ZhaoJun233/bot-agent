using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Plugins;

// Isolated synthetic root: the store binds StoragePaths + AppDatabase exactly like the host does.
var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "botagent-plugin-settings-probe-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
Environment.SetEnvironmentVariable("BOTAGENT_FILE_LOG", "0");
Environment.SetEnvironmentVariable("QQCHAT_FILE_LOG", "0");

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
    checks++;
}

void Expect(Func<object?> action, string code, string label)
{
    try { _ = action(); }
    catch (PluginSettingsException ex) when (ex.Code == code) { checks++; return; }
    throw new InvalidOperationException(label);
}

static PluginSettingDefinition Field(string key, string label, string type, string? defaultValue = null,
    bool required = false, string[]? choices = null)
    => new(key, label, type, defaultValue, required, choices);

static PluginPackageManifest Manifest(string id, string label = "Greeting")
    => new(id, "1.0.0", "probe.dll", "Probe.SettingsPlugin", new string('a', 64),
        "https://github.com/example/probe/releases/download/v1.0.0/probe.zip",
        Settings: new[]
        {
            Field("greeting", label, "string", "hello"),
            Field("verbose", "Verbose", "boolean", "true"),
            Field("retries", "Retries", "number", "3"),
            Field("mode", "Mode", "select", "fast", choices: new[] { "fast", "slow" }),
            Field("token", "Token", "secret", required: true),
            Field("optionalToken", "Optional token", "secret")
        });

static JsonElement Values(Dictionary<string, object?> values) => JsonSerializer.SerializeToElement(values);

try
{
    AppDatabase.Initialize();
    var store = new PluginSettingsStore();
    var manifest = Manifest("probe.settings");

    // Fresh read: declarative defaults, no secrets, restart required because nothing was applied yet.
    var initial = store.ReadManifest(manifest);
    Check(initial.Revision == 0, "fresh_revision_zero");
    Check(initial.RestartRequired, "fresh_restart_required");
    Check(initial.ConfiguredSecrets.Count == 0, "fresh_no_secrets");
    Check(initial.Values.Count == 4 && !initial.Values.ContainsKey("token"), "fresh_values_hide_secrets");
    Check(initial.Values["greeting"].GetString() == "hello", "fresh_default_string");
    Check(initial.Values["retries"].GetInt32() == 3, "fresh_default_number");
    Check(initial.Schema.Count == 6, "schema_field_count");

    // Missing required secret only fails the runtime resolution, never the operator view.
    Expect(() => store.ResolveRuntimeValues(manifest), PluginSettingsContract.RequiredMissing, "required_secret_missing");

    var first = store.Save(manifest, 0, "idem-0001", Values(new() { ["greeting"] = "hi", ["token"] = "s3cret", ["mode"] = "slow" }), "probe-caller");
    Check(first.Revision == 1, "first_save_revision");
    Check(first.RestartRequired, "first_save_restart");
    Check(first.ReceiptId.Length == 64, "receipt_is_hmac");

    // Exact replay returns the same receipt instead of a second revision.
    var replay = store.Save(manifest, 0, "idem-0001", Values(new() { ["greeting"] = "hi", ["token"] = "s3cret", ["mode"] = "slow" }), "probe-caller");
    Check(replay.ReceiptId == first.ReceiptId && replay.Revision == first.Revision, "idempotent_replay");
    // Same key, different content is a conflicting reuse.
    Expect(() => store.Save(manifest, 1, "idem-0001", Values(new() { ["greeting"] = "other" }), "probe-caller"),
        "plugin_settings_idempotency_conflict", "idempotency_conflict");
    // A new key against a stale revision is rejected, not silently merged.
    Expect(() => store.Save(manifest, 0, "idem-0002", Values(new() { ["greeting"] = "stale" }), "probe-caller"),
        "plugin_settings_revision_conflict", "revision_conflict");

    var saved = store.ReadManifest(manifest);
    Check(saved.Revision == 1 && saved.Values["greeting"].GetString() == "hi", "saved_override");
    Check(saved.Values["retries"].GetInt32() == 3, "saved_keeps_defaults");
    Check(saved.ConfiguredSecrets.Count == 1 && saved.ConfiguredSecrets[0] == "token", "saved_secret_configured");
    Check(!saved.Values.ContainsKey("token"), "saved_never_echoes_secret");

    // Host-only runtime resolution carries the secret and must be acknowledged afterwards.
    Check(store.MarkApplied(manifest, 1) == false, "apply_requires_resolution");
    var runtime = store.ResolveRuntimeValues(manifest, out var resolvedRevision);
    Check(resolvedRevision == 1, "runtime_revision");
    using (var document = JsonDocument.Parse(runtime))
    {
        Check(document.RootElement.GetProperty("greeting").GetString() == "hi", "runtime_greeting");
        Check(document.RootElement.GetProperty("token").GetString() == "s3cret", "runtime_secret");
        Check(document.RootElement.GetProperty("mode").GetString() == "slow", "runtime_select");
    }
    Check(store.MarkApplied(manifest, 1), "apply_after_resolution");
    Check(store.MarkApplied(manifest, 2) == false, "apply_rejects_unknown_revision");
    Check(store.ReadManifest(manifest).RestartRequired == false, "restart_cleared_after_apply");

    // A second field set, then an explicit null that deletes only that secret row.
    var second = store.Save(manifest, 1, "idem-0003", Values(new() { ["optionalToken"] = "abc" }), "probe-caller");
    Check(second.Revision == 2, "second_save_revision");
    Check(store.ReadManifest(manifest).ConfiguredSecrets.Count == 2, "two_secrets_configured");
    var third = store.Save(manifest, 2, "idem-0004", Values(new() { ["optionalToken"] = null }), "probe-caller");
    Check(third.Revision == 3, "delete_save_revision");
    var afterDelete = store.ReadManifest(manifest);
    Check(afterDelete.ConfiguredSecrets.Count == 1 && afterDelete.ConfiguredSecrets[0] == "token", "secret_deleted");
    Check(afterDelete.Values["greeting"].GetString() == "hi", "overrides_survive_delete");

    // Unknown, mistyped or out-of-choice values are refused before any write.
    Expect(() => store.Save(manifest, 4, "idem-0005", Values(new() { ["nope"] = "x" }), "probe-caller"),
        PluginSettingsContract.InvalidValues, "unknown_key_rejected");
    Expect(() => store.Save(manifest, 4, "idem-0006", Values(new() { ["retries"] = "three" }), "probe-caller"),
        PluginSettingsContract.InvalidValues, "type_mismatch_rejected");
    Expect(() => store.Save(manifest, 4, "idem-0007", Values(new() { ["mode"] = "turbo" }), "probe-caller"),
        PluginSettingsContract.InvalidValues, "choice_mismatch_rejected");
    // A required secret must not be blanked; an optional plain string may be blanked.
    Expect(() => store.Save(manifest, 3, "idem-0008", Values(new() { ["token"] = "  " }), "probe-caller"),
        PluginSettingsContract.InvalidValues, "blank_required_secret_rejected");
    var blankOptional = store.Save(manifest, 3, "idem-0008b", Values(new() { ["greeting"] = "  " }), "probe-caller");
    Check(blankOptional.Revision == 4, "blank_optional_string_allowed");
    Expect(() => store.Save(manifest, 3, "  ", Values(new() { ["greeting"] = "ok" }), "probe-caller"),
        "plugin_settings_invalid_request", "blank_idempotency_key");
    Expect(() => store.Save(manifest, 4, "idem-0009", Values(new() { ["greeting"] = "ok" }), " "),
        "plugin_settings_invalid_request", "blank_caller_scope");
    Check(store.ReadManifest(manifest).Revision == 4, "rejections_leave_state_untouched");

    // A changed schema fails closed and preserves the stored values for review.
    Expect(() => store.ReadManifest(Manifest("probe.settings", "Renamed")), "plugin_settings_schema_mismatch", "schema_mismatch");

    // Declarative schema defects are rejected before anything touches storage.
    Func<PluginSettingDefinition[], PluginPackageManifest> broken = fields => new PluginPackageManifest(
        "probe.broken", "1.0.0", "probe.dll", "Probe.SettingsPlugin", new string('b', 64),
        "https://github.com/example/probe/releases/download/v1.0.0/probe.zip", Settings: fields);
    Expect(() => store.ReadManifest(broken(new[] { Field("1bad", "Bad", "string") })), PluginSettingsContract.InvalidSchema, "invalid_key");
    Expect(() => store.ReadManifest(broken(new[] { Field("a", "A", "string"), Field("a", "B", "string") })), PluginSettingsContract.InvalidSchema, "duplicate_key");
    Expect(() => store.ReadManifest(broken(new[] { Field("a", "A", "secret", "leak") })), PluginSettingsContract.InvalidSchema, "secret_with_default");
    Expect(() => store.ReadManifest(broken(new[] { Field("a", "A", "select") })), PluginSettingsContract.InvalidSchema, "select_without_choices");
    Expect(() => store.ReadManifest(broken(new[] { Field("a", "A", "select", "x", choices: new[] { "x", "x" }) })), PluginSettingsContract.InvalidSchema, "duplicate_choice");
    Expect(() => store.ReadManifest(broken(new[] { Field("a", "A", "text") })), PluginSettingsContract.InvalidSchema, "unknown_type");
    Expect(() => store.ReadManifest(broken(new[] { Field("a", "A", "boolean", "maybe") })), PluginSettingsContract.InvalidSchema, "invalid_default");

    // A package id reserved for built-ins can never own settings.
    Expect(() => store.ReadManifest(Manifest("preset.websearch")), PluginSettingsContract.InvalidSchema, "reserved_id");

    Console.WriteLine("PluginSettingsProbe OK checks=" + checks);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("PluginSettingsProbe FAILED: " + ex);
    return 1;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}
