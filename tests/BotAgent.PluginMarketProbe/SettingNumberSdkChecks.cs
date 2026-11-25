using System.Text.Json;
using BotAgent.Domain.Plugins;

internal partial class Program
{
    internal static Task<int> RunSettingNumberSdkChecks()
    {
        var checks = 0;
        void Check(bool valid, string label)
        { if (!valid) throw new InvalidOperationException(label); checks++; }
        void Reject(Action action, string code)
        {
            try { action(); }
            catch (PluginSettingsException error) when (error.Code == code) { checks++; return; }
            throw new InvalidOperationException("setting_number_expected_" + code);
        }
        static JsonElement Value(string json)
        { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
        var integer = new PluginSettingDefinition("limit", "Limit", "number", Required: true)
        { NumberConstraints = new(Integer: true) };
        Check(typeof(PluginSettingValidation).Assembly == typeof(PluginSettingDefinition).Assembly
            && typeof(PluginSettingsException).Assembly == typeof(PluginSettingDefinition).Assembly,
            "pure rules and failures are provided by the same independent SDK");
        foreach (var json in new[] { "0", "-0", "2147483648", "9007199254740991", "-9007199254740991" })
        { PluginSettingValidation.ValidateValue(integer, Value(json), false); checks++; }
        foreach (var json in new[] { "1.0", "1e0", "0.5", "9007199254740992", "-9007199254740992", "\"1\"", "true", "[]", "{}" })
            Reject(() => PluginSettingValidation.ValidateValue(integer, Value(json), false), PluginSettingValidation.InvalidValues);
        Reject(() => PluginSettingValidation.ValidateValue(integer, Value("null"), false), PluginSettingValidation.RequiredMissing);
        PluginSettingValidation.ValidateValue(integer, Value("null"), true); checks++;
        var ranged = integer with { NumberConstraints = new(0.5, 10.5), DefaultValue = "1.25" };
        Check(PluginSettingValidation.ReadSchema([ranged]).Single().DefaultValue == "1.25",
            "inclusive non-integer limits support finite decimal defaults");
        foreach (var json in new[] { "0.5", "1.25", "10.5", "1e0" })
        { PluginSettingValidation.ValidateValue(ranged, Value(json), false); checks++; }
        foreach (var json in new[] { "0.49", "10.51", "1e999" })
            Reject(() => PluginSettingValidation.ValidateValue(ranged, Value(json), false), PluginSettingValidation.InvalidValues);
        foreach (var field in new[] { integer with { Type = "string" }, integer with { NumberConstraints = new(double.NaN) },
            integer with { NumberConstraints = new(Maximum: double.PositiveInfinity) }, integer with { NumberConstraints = new(10, 1) },
            integer with { DefaultValue = "1.5" }, integer with { DefaultValue = "9007199254740992" }, integer with { DefaultValue = "1.0" } })
            Reject(() => PluginSettingValidation.ReadSchema([field]), PluginSettingValidation.InvalidSchema);
        Reject(() => PluginSettingValidation.ValidateValue(integer with { Type = "string" }, Value("\"safe\""), false),
            PluginSettingValidation.InvalidValues);
        foreach (var json in new[] { "{\"minimum\":0,\"minimum\":1}", "{\"minimum\":0,\"Minimum\":1}",
            "{\"integer\":null}", "{\"minimum\":\"1\"}", "{\"maximum\":1e999}", "{\"future\":1}", "[]" })
        {
            try { JsonSerializer.Deserialize<PluginSettingNumberConstraints>(json); }
            catch (JsonException) { checks++; continue; }
            throw new InvalidOperationException("ambiguous_number_constraints_accepted");
        }
        Check(JsonSerializer.Serialize(new PluginSettingNumberConstraints()) ==
            "{\"minimum\":null,\"maximum\":null,\"integer\":false}", "missing rule properties have one canonical representation");
        Console.WriteLine($"PASS setting number SDK: {checks} checks");
        return Task.FromResult(checks);
    }
}
