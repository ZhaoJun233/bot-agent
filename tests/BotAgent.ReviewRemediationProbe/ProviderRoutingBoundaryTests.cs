using System.Reflection;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Model;
using BotAgent.Services.Resilience;

namespace BotAgent.ReviewRemediationProbe;

/// <summary>
/// #132：运行期路由与「发现模型」共用同一条硬边界。以前测试路由只读库、不比对装配快照，
/// 于是面板测通、线上拒绝，排障时看不出差异。这里用反射直接钉住判定表，防止两条通道再次分叉。
/// </summary>
internal static class ProviderRoutingBoundaryTests
{
    internal static Task RunAsync(Action<bool, string> check)
    {
        var type = typeof(BotAgent.Services.SettingsBox).Assembly.GetType("BotAgent.Host.CompositionRoot")
            ?? throw new InvalidOperationException("composition_root_missing");
        var method = type.GetMethod("ProviderRouteBoundary", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("provider_route_boundary_missing");

        check(Boundary(method, Provider(), Provider(), true) is null,
            "同一条装配事实与库里一致时路由边界放行");
        check(Boundary(method, Provider(protocol: ModelProtocol.Ollama), Provider(), true) == "provider_protocol_changed",
            "协议变化是两条通道共同的硬边界");
        check(Boundary(method, Provider(options: new ModelProtocolOptions()), Provider(), true) == "provider_options_changed",
            "协议选项变化是两条通道共同的硬边界");
        check(Boundary(method, Provider(baseUrl: "https://example.org/v1"), Provider(), true) == "provider_endpoint_changed",
            "非 primary 端点变化是硬边界");
        check(Boundary(method, Provider(baseUrl: "https://example.org/v1"), Provider(baseUrl: "https://example.org/v1"), true) is null,
            "非 primary 端点一致时不误报");
        check(Boundary(method, Provider(id: "primary", baseUrl: "https://example.org/v1"), Provider(id: "primary"), true) is null,
            "primary 端点属于热更新字段，不算硬边界");
        check(Boundary(method, Provider(enabled: false), Provider(), true) == "provider_disabled",
            "运行期路由拒绝未启用的供应商");
        check(Boundary(method, Provider(enabled: false), Provider(), false) is null,
            "发现模型允许试连未启用的供应商（唯一更宽的一处）");
        check(Boundary(method, Provider(id: " secondary"), Provider(id: " secondary"), true) == "provider_id_not_trimmed",
            "带空白的供应商 id 不会被当成有效路由");
        return Task.CompletedTask;
    }

    private static string? Boundary(MethodInfo method, PersistedModelProvider desired, PersistedModelProvider? captured,
        bool requireEnabled)
        => (string?)method.Invoke(null, [desired, captured, requireEnabled]);

    private static PersistedModelProvider Provider(string id = "secondary", bool enabled = true,
        ModelProtocol protocol = ModelProtocol.OpenAiChat, ModelProtocolOptions? options = null,
        string baseUrl = "https://example.com/v1", string modelName = "example-model")
        => new(id, 1, "Synthetic Provider", baseUrl, modelName, "secret:apiKey", enabled, protocol, options,
            ProviderCircuitState.Closed, 0, null, DateTimeOffset.UnixEpoch);
}
