using System.Text;

namespace BotAgent.Services.OneBot;

/// <summary>Bound complete-message bytes before strict UTF8 decoding, including binary messages we discard.</summary>
internal sealed class WsMessageBuffer : IDisposable
{
    internal const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly MemoryStream _bytes = new();

    public void EnsureCanAppend(ulong count)
    {
        if (count > (ulong)(MaxMessageBytes - _bytes.Length))
            throw new InvalidDataException("websocket_message_too_large");
    }

    public void Append(byte[] bytes, int count)
    {
        EnsureCanAppend((ulong)count);
        _bytes.Write(bytes, 0, count);
    }

    public string DecodeText() => StrictUtf8.GetString(_bytes.GetBuffer(), 0, (int)_bytes.Length);

    public static string DecodeText(byte[] bytes, int offset, int count) => StrictUtf8.GetString(bytes, offset, count);

    public void Reset() => _bytes.SetLength(0);
    public void Dispose() => _bytes.Dispose();
}
