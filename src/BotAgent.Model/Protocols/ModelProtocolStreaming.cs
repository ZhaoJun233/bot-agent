using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Model;

namespace BotAgent.Model.Protocols;

/// <summary>Protocol capabilities, not a promise that every model implements tools or vision.</summary>
public sealed record ModelProtocolCapabilities(bool Tools, bool Images, bool Streaming, bool RequiresApiKey);

/// <summary>Pure event decoding for optional streaming consumers; no network, retry, or logging policy.</summary>
public static class ModelProtocolStreaming
{
    private const int MaxEventCharacters = 1024 * 1024;

    public static ModelProtocolCapabilities Capabilities(ModelProtocol protocol)
    {
        if (!Enum.IsDefined(protocol)) throw new ModelFault("model_protocol_unsupported");
        return new(true, true, true, protocol != ModelProtocol.Ollama);
    }

    /// <summary>
    /// Decodes SSE (or Ollama NDJSON) to chat-completion chunks. Tool indexes persist across events.
    /// The caller retains stream ownership and is responsible for HTTP status and overall timeout policy.
    /// The existing buffered transport deliberately does not consume this opt-in interface.
    /// </summary>
    public static async IAsyncEnumerable<JsonObject> ReadAsync(Stream source, ModelProtocol protocol,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ = Capabilities(protocol);
        ct.ThrowIfCancellationRequested();
        using var reader = new StreamReader(source, Encoding.UTF8, true, 4096, leaveOpen: true);
        var decoder = new ProtocolStreamDecoder(protocol);
        var data = new StringBuilder();
        await foreach (var line in LinesAsync(reader, ct).ConfigureAwait(false))
        {
            if (protocol == ModelProtocol.Ollama)
            {
                if (!string.IsNullOrWhiteSpace(line)) yield return decoder.Decode(line);
                continue;
            }
            if (line.Length == 0)
            {
                if (data.Length == 0) continue;
                var value = data.ToString();
                data.Clear();
                if (value.Trim() == "[DONE]") yield break;
                yield return decoder.Decode(value);
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                var offset = line.Length > 5 && line[5] == ' ' ? 6 : 5;
                if (data.Length + line.Length - offset > MaxEventCharacters) throw new ModelFault("model_response_too_large");
                data.Append(line.AsSpan(offset));
            }
        }
        if (data.Length > 0 && data.ToString().Trim() != "[DONE]") yield return decoder.Decode(data.ToString());
    }

    // Bound incomplete lines before allocating a whole untrusted event, including CR-only SSE framing.
    private static async IAsyncEnumerable<string> LinesAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var afterCarriageReturn = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ch = buffer[i];
                if (ch == '\n' && afterCarriageReturn) { afterCarriageReturn = false; continue; }
                afterCarriageReturn = ch == '\r';
                if (ch is '\r' or '\n')
                {
                    yield return line.ToString();
                    line.Clear();
                }
                else
                {
                    if (line.Length >= MaxEventCharacters) throw new ModelFault("model_response_too_large");
                    line.Append(ch);
                }
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
