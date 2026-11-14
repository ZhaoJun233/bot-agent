using BotAgent.Domain.Conversation;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Stickers;
using BotAgent.Services.Conversations;
using BotAgent.Services.Plugins;
using BotAgent.Services.Ports;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

internal sealed partial class ReplyDelivery
{
    private async Task<CadenceSendReport> SendTextAfterVoiceAsync(BotConversation conversation,
        bool isGroup, long targetId, string? text, long? replyTo, bool directAddress, bool voiceSent)
    {
        if (text is null) return CadenceSendReport.None;
        try
        {
            return await _plain.SendWithCadenceAsync(conversation.SourceKey, isGroup, targetId, text, replyTo, directAddress);
        }
        catch (Exception) when (voiceSent && ReplyCommitBoundary.IsActive)
        {
            return new CadenceSendReport(Array.Empty<string>(), "commit_interrupted");
        }
    }

    private async Task<(SendResult Image, bool PokeSent)> SendExtrasAsync(BotConversation conversation,
        IReadOnlyList<ChatMessage> context, ChatCapabilitySet caps, EffectivePlatformPolicy platformPolicy,
        bool isGroup, long targetId, long? replyTo, StickerRecord? sticker, long? pokeTarget, bool chatSent)
    {
        var image = new SendResult(false);
        var pokeSent = false;
        try
        {
            if (sticker is not null)
            {
                using var validation = ReplyCommitBoundary.EnterValidation(() =>
                    ValidateModality(conversation, null, platformPolicy, "image", PluginGate.StickersCapability, "sticker.send"));
                image = await SendStickerAsync(isGroup, targetId, sticker, replyTo, conversation.SourceKey);
                if (ReplyDeliveryResults.Confirmed(image)) _stickers.Store.MarkUsed(sticker.Id);
            }
            pokeSent = !ReplyDeliveryResults.StopsFurtherSending(image.Delivery) && pokeTarget is long pokeUserId &&
                await TryPokeAsync(conversation, context, caps, platformPolicy, isGroup, targetId, pokeUserId);
        }
        catch (Exception) when ((chatSent || ReplyDeliveryResults.Confirmed(image)) && ReplyCommitBoundary.IsActive)
        {
            // Preserve earlier acknowledgements without treating an interrupted reply as complete.
        }
        return (image, pokeSent);
    }

    /// <summary>Keep modality-specific live checks active through transport preparation.</summary>
    private void ValidateModality(BotConversation conversation, AppSettings? fallbackSettings,
        EffectivePlatformPolicy fallbackPolicy, string feature, string pluginFeature, string toolId)
    {
        var live = _settingsBox?.Current ?? fallbackSettings;
        var enabled = feature switch
        {
            "voice" => live?.EnableVoice ?? true,
            "image" => live?.EnableStickers ?? true,
            "poke" => live?.EnablePoke ?? true,
            _ => false,
        };
        var policy = _platformPolicies?.Resolve(PlatformSourceExecution.Current?.Context ?? fallbackPolicy.Context) ?? fallbackPolicy;
        if (_hooks.IsDisposed() || _riskBackoff?.IsActive(conversation.SourceKey) == true
            || !policy.Feature(feature, enabled).Enabled || !_allowsFeature(pluginFeature)
            || !_approvals.Capabilities.Check(toolId, conversation.SourceKey).Allow)
            throw new OperationCanceledException("reply_modality_disabled");
    }

}
