using BotAgent.Engine.Runtime;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Tools;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

/// <summary>Intersect pinned turn grants with live platform/plugin/source revocation, never expanding a turn.</summary>
public sealed class ReplyToolAvailability(
    PluginGate? plugins = null,
    IPlatformPolicyResolver? policies = null,
    Func<bool>? isDisposed = null,
    Func<bool>? webReady = null)
{
    public bool IsCurrent => isDisposed?.Invoke() != true && PlatformSourceExecution.Current is not { IsCurrent: false };

    public bool Allows(AppSettings snapshot, EffectivePlatformPolicy? pinned, string toolId)
    {
        if (!IsCurrent || plugins?.AllowsTool(toolId) == false) return false;
        var (feature, enabled) = toolId switch
        {
            "web.search" or "web.read" => ("websearch", snapshot.EnableWebSearch && webReady?.Invoke() != false),
            "music.listen" or "music.share" => ("music", snapshot.EnableMusic),
            "voice.speak" => ("voice", snapshot.EnableVoice),
            "sticker.send" => ("stickers", snapshot.EnableStickers),
            "poke.send" => ("poke", snapshot.EnablePoke),
            ApprovalFlow.FixedToolId => ("text", snapshot.EnableApprovals),
            ApprovalFlow.QuestionToolId => ("text", snapshot.EnableQuestions),
            "chat.reply" => ("text", true),
            _ => ("text", false),
        };
        if (!enabled || pinned?.Feature(feature, enabled).Enabled == false) return false;
        var context = PlatformSourceExecution.Current?.Context ?? pinned?.Context;
        return policies is null || context is not null && policies.Resolve(context).Feature(feature, enabled).Enabled;
    }

    public string Render(AppSettings snapshot, EffectivePlatformPolicy pinned, ChatCapabilitySet caps)
        => ToolPromptText.Render(ChatToolSpecs.All.Where(spec => Allows(snapshot, pinned, spec.Id)).ToArray(), caps.Policy);
}
