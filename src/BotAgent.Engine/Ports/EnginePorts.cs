using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Domain.Messaging;
using BotAgent.Services.Music;
using BotAgent.Services.OneBot;

namespace BotAgent.Services.Panel
{
    /// <summary>Panel-facing notifications emitted by Engine; the Host owns the transport.</summary>
    public interface IPanelNotifier
    {
        void EmitLog(string message);
        void SetThinking(BotConversation conversation, bool thinking);
        void NotifyMessageAdded(string sourceKey, ChatMessage message);
        void NotifyConversationsChanged();
        void NotifyStateChanged();
    }
}

namespace BotAgent.Services.Agent
{
    /// <summary>Explicit-command backend; Engine owns parsing/routing, the Host owns the runner.</summary>
    public interface IAgentCommandService
    {
        bool TryParseCommand(string rawText, out string payload);
        Task RunCommandSafeAsync(BotConversation conversation, QqChatMessage message, string payload);
    }
}

namespace BotAgent.Services.Links
{
    /// <summary>Link preview capability; network IO stays in the Host adapter.</summary>
    public interface ILinkPreviewer
    {
        Task<string?> DescribeAsync(IReadOnlyList<string> urls, CancellationToken ct);
    }
}

namespace BotAgent.Services.Voice
{
    /// <summary>Voice synthesis capability; transport/secrets stay in the Host adapter.</summary>
    public interface IVoiceSynthesis
    {
        string? BuildSpeakUrl(string text, string? voiceOverride = null, int? speedOverride = null,
            string? emotionOverride = null, int? pitchOverride = null, int? volOverride = null);

        Task<(byte[]? Data, string? Error)> SynthesizeAsync(string text, string? voiceOverride,
            int? speedOverride, CancellationToken ct);
    }
}

namespace BotAgent.Services.Music
{
    /// <summary>Music catalog capability; network playback stays in the Host adapter.</summary>
    public interface IMusicCatalog
    {
        Task<string?> DescribeByNameAsync(string keyword, string senderName, CancellationToken ct);
        Task<string?> ResolveSongIdByNameAsync(string keyword, CancellationToken ct);
        Task<MusicShare?> SearchAsync(string keyword, CancellationToken ct);
        Task<string?> DescribeAsync(MusicShare share, string senderName, CancellationToken ct);
    }
}

namespace BotAgent.Services.Net
{
    /// <summary>Web research capability; HTTP and search backends stay in the Host adapter.</summary>
    public interface IWebSearch
    {
        Task<WebSearchResult?> SearchAsync(string query, CancellationToken ct);
        Task<(string? Text, string? Error)> ReadPageAsync(string url, CancellationToken ct);
    }
}

namespace BotAgent.Services.Voice
{
    /// <summary>Voice pacing/decision use case owned by the Host until the capability migration lands.</summary>
    public interface IVoiceUseCase
    {
        int SuggestIntervalSeconds { get; }
        string? ClientVoiceName { get; }
        bool Allow(string sourceKey, out string reason);
        List<string> BuildSpeakUrls(IReadOnlyList<string> parts, string? emotion, double? speed, int? pitch);
        void MarkSent(string sourceKey);
    }
}

namespace BotAgent.Services.Music
{
    /// <summary>Music orchestration use case owned by the Host until the capability migration lands.</summary>
    public interface IMusicUseCase
    {
        bool IsReady { get; }
        Task<string?> DescribeShareAsync(MusicShare share, string sender);
        void AddNote(string sourceKey, string note);
        string? TakeNote(string sourceKey);
        bool TryBeginListen(string sourceKey, int cooldownSeconds, out string reason);
        Task<string?> ListenAsync(string sourceKey, string song, string requester, CancellationToken ct = default);
        Task<bool> ShareAsync(string song, bool isGroup, long targetId, CancellationToken ct = default);
    }
}

namespace BotAgent.Services.Net
{
    /// <summary>Research orchestration use case owned by the Host until the capability migration lands.</summary>
    public interface IResearchUseCase
    {
        bool IsReady { get; }
        bool TryBeginSearch(string sourceKey, int cooldownSeconds, out string reason);
        Task<WebSearchResult?> SearchAsync(string sourceKey, string query, CancellationToken ct = default);
        Task<string?> ReadPageAsync(string sourceKey, string url, CancellationToken ct = default);
        void AddNote(string sourceKey, string note);
        void KeepNote(string sourceKey, string? note, string why);
        string? TakeNote(string sourceKey);
    }
}
