using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Qq;

namespace BotAgent.Services.Official;

public sealed partial class OfficialBotGateway
{
    private sealed record GroupSelf(string? MemberId, DateTimeOffset ValidUntil);
    private readonly object _selfGate = new();
    private readonly Dictionary<string, GroupSelf> _groupSelf = new(StringComparer.Ordinal);
    private long _selfGeneration;
    private const int SelfCapacity = 256;
    private const int SelfResponseLimit = 16 * 1024;

    private async Task HandleDispatchAsync(string? type, JsonObject? data, JsonNode? raw, CancellationToken ct)
    {
        if (!TryLoadIdentities()) return;
        if (type is "GROUP_MESSAGE_CREATE" or "GROUP_AT_MESSAGE_CREATE" && data is not null
            && ReadSenderId(data, "member_openid") is { } sender)
        {
            try
            {
                var group = ReadIdentityId(data["group_openid"]);
                var message = MessageIdentity(true, group, ReadIdentityId(data["id"]), OfficialMessageDirection.Inbound, OfficialReferenceFields.ReadMessageIndex(data));
                _ = new OfficialIdentityKey(new(_applicationId, OfficialIdentityKind.GroupMember, message.Scope.Conversation), sender);
                if (!await RefreshGroupSelfAsync(group, ct).ConfigureAwait(false)) return;
            }
            catch (Exception error) when (IsIdentityFailure(error)) { RejectIdentity(error); return; }
        }
        ct.ThrowIfCancellationRequested();
        HandleDispatch(type, data, raw);
    }

    // A persisted BotSelf alias records provenance; only this fresh authenticated response proves current self.
    private async Task<bool> RefreshGroupSelfAsync(string group, CancellationToken ct)
    {
        long generation;
        lock (_selfGate)
        {
            if (_groupSelf.TryGetValue(group, out var cached) && Clock.Now < cached.ValidUntil) return true;
            _groupSelf.Remove(group);
            generation = _selfGeneration;
        }
        string? member = null;
        var reason = "self_identity_unavailable";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var token = await EnsureTokenAsync(false, timeout.Token).ConfigureAwait(false);
            if (!ApplicationIsCurrent()) return false;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/v2/groups/{Uri.EscapeDataString(group)}/bot_state");
            Auth(request, token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var data = await ReadSelfResponseAsync(response.Content, timeout.Token).ConfigureAwait(false);
                member = ReadIdentityId(data?["member_openid"]);
                _ = new OfficialIdentityKey(new(_applicationId, OfficialIdentityKind.BotSelf, NativeConversation(true, group)), member);
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) reason = "capability_not_granted";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { reason = "self_identity_timeout"; }
        catch (Exception error) when (error is HttpRequestException or JsonException || IsIdentityFailure(error))
        { member = null; }
        ct.ThrowIfCancellationRequested();
        lock (_selfGate)
        {
            if (generation != _selfGeneration || !ApplicationIsCurrent()) return false;
            if (member is not null)
            {
                try { _ids.Value.AliasFor(new(new(_applicationId, OfficialIdentityKind.BotSelf, NativeConversation(true, group)), member)); }
                catch (Exception error) when (IsIdentityFailure(error)) { member = null; reason = "self_identity_store_unavailable"; }
            }
            if (_groupSelf.Count >= SelfCapacity)
                _groupSelf.Remove(_groupSelf.MinBy(pair => pair.Value.ValidUntil).Key);
            _groupSelf[group] = new(member, Clock.Now + (member is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5)));
        }
        if (member is null) Log("官方自身身份暂不可用 reason=" + reason);
        return true;
    }

    private static async Task<JsonObject?> ReadSelfResponseAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > SelfResponseLimit) throw new InvalidDataException("self_response_too_large");
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var bytes = new byte[SelfResponseLimit + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > SelfResponseLimit) throw new InvalidDataException("self_response_too_large");
        return JsonNode.Parse(Encoding.UTF8.GetString(bytes, 0, length)) as JsonObject;
    }

    private string? CurrentGroupSelf(string group)
    {
        lock (_selfGate) return _groupSelf.TryGetValue(group, out var value) && Clock.Now < value.ValidUntil ? value.MemberId : null;
    }

    private void InvalidateGroupSelf(string? group = null)
    {
        lock (_selfGate)
        {
            _selfGeneration++;
            if (group is null) _groupSelf.Clear();
            else _groupSelf.Remove(group);
        }
    }

    private bool RejectSelfSender(string? self, string sender)
    {
        if (self is null || sender != self) return false;
        Log("官方入站拒绝 reason=self_message");
        return true;
    }

    private static bool MentionsSelf(JsonObject data, string? self)
    {
        if (self is null || data["mentions"] is not JsonArray mentions) return false;
        foreach (var item in mentions.OfType<JsonObject>())
        {
            static string? Id(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var id) ? id : null;
            var member = Id(item["member_openid"]);
            var generic = Id(item["id"]);
            if (item.ContainsKey("member_openid") && member != self) continue;
            if (item.ContainsKey("id") && generic != self) continue;
            if (member == self || generic == self) return true;
        }
        return false;
    }

    private static string StripLeadingSelfMention(string text, string? self)
    {
        if (self is null) return text;
        var trimmed = text.TrimStart();
        foreach (var prefix in new[] { "<@" + self + ">", "<@!" + self + ">" })
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal)) return trimmed[prefix.Length..].TrimStart();
        return text;
    }
}
