using System.Buffers.Binary;

namespace BotAgent.Services.OneBot;

/// <summary>RFC6455 client frames: validate advertised lengths before allocation and retain message state across controls.</summary>
internal sealed class ReverseWsFrameReader : IDisposable
{
    private readonly Stream _stream;
    private readonly WsMessageBuffer _message = new();
    private int _messageOpcode;

    public ReverseWsFrameReader(Stream stream) => _stream = stream;

    public async Task<(int Opcode, byte[] Payload, string? Text)> ReadAsync(CancellationToken ct)
    {
        var header = new byte[2];
        await _stream.ReadExactlyAsync(header, ct);
        bool final = (header[0] & 0x80) != 0;
        int opcode = header[0] & 0x0F;
        if ((header[0] & 0x70) != 0 || (header[1] & 0x80) == 0 ||
            opcode is not (0 or 1 or 2 or 8 or 9 or 10))
            throw new InvalidDataException("invalid_websocket_frame_header");

        ulong length = await ReadLengthAsync(header[1] & 0x7F, ct);
        if (opcode >= 8)
        {
            if (!final || length > 125)
                throw new InvalidDataException("invalid_websocket_control_frame");
        }
        else
        {
            ValidateDataHeader(opcode, length);
        }

        // Validation above rejects frame and aggregate overflow without reading the mask or payload.
        var mask = new byte[4];
        await _stream.ReadExactlyAsync(mask, ct);
        var payload = new byte[(int)length];
        await _stream.ReadExactlyAsync(payload, ct);
        for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

        if (opcode == 8) ValidateClose(payload);
        if (opcode >= 8) return (opcode, payload, null);

        _message.Append(payload, payload.Length);
        if (!final) return (opcode, payload, null);
        var text = _messageOpcode == 1 ? _message.DecodeText() : null;
        _message.Reset();
        _messageOpcode = 0;
        return (opcode, payload, text);
    }

    private async Task<ulong> ReadLengthAsync(int marker, CancellationToken ct)
    {
        if (marker < 126) return (ulong)marker;
        var bytes = new byte[marker == 126 ? 2 : 8];
        await _stream.ReadExactlyAsync(bytes, ct);
        ulong length = marker == 126
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes)
            : BinaryPrimitives.ReadUInt64BigEndian(bytes);
        if ((marker == 126 && length < 126) ||
            (marker == 127 && (length <= ushort.MaxValue || (bytes[0] & 0x80) != 0)))
            throw new InvalidDataException("invalid_websocket_frame_length");
        return length;
    }

    private void ValidateDataHeader(int opcode, ulong length)
    {
        if (opcode == 0)
        {
            if (_messageOpcode == 0) throw new InvalidDataException("unexpected_websocket_continuation");
        }
        else
        {
            if (_messageOpcode != 0) throw new InvalidDataException("unfinished_websocket_message");
            _messageOpcode = opcode;
        }
        _message.EnsureCanAppend(length);
    }

    private static void ValidateClose(byte[] payload)
    {
        if (payload.Length == 0) return;
        if (payload.Length == 1) throw new InvalidDataException("invalid_websocket_close_payload");
        int status = BinaryPrimitives.ReadUInt16BigEndian(payload);
        if (status < 1000 || status >= 5000 || status is 1004 or 1005 or 1006 or 1015 || status is >= 1016 and < 3000)
            throw new InvalidDataException("invalid_websocket_close_status");
        _ = WsMessageBuffer.DecodeText(payload, 2, payload.Length - 2);
    }

    public void Dispose() => _message.Dispose();
}
