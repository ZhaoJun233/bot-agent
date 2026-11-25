using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace BotAgent.Services.Official;

/// <summary>Writes bounded Base64 chunks without constructing a second, escaped JSON copy of the media.</summary>
internal sealed class OfficialMediaContent : HttpContent
{
    private const int ChunkBytes = 32766; // Whole Base64 triplets; padding belongs only to the last chunk.
    private static readonly byte[] Suffix = "\",\"srv_send_msg\":false}"u8.ToArray();
    private readonly byte[] _prefix;
    private readonly ReadOnlyMemory<byte> _data;

    public OfficialMediaContent(int fileType, ReadOnlyMemory<byte> data)
    {
        if (fileType is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(fileType));
        _data = data;
        _prefix = Encoding.UTF8.GetBytes("{\"file_type\":" + (fileType == 1 ? "1" : "3") + ",\"file_data\":\"");
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _prefix.Length + ((_data.Length + 2L) / 3 * 4) + Suffix.Length;
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        await stream.WriteAsync(_prefix, ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(Base64.GetMaxEncodedToUtf8Length(ChunkBytes));
        try
        {
            for (var offset = 0; offset < _data.Length; offset += ChunkBytes)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = _data.Slice(offset, Math.Min(ChunkBytes, _data.Length - offset));
                var status = Base64.EncodeToUtf8(chunk.Span, buffer, out var consumed, out var written);
                if (status != OperationStatus.Done || consumed != chunk.Length) throw new InvalidOperationException("media_encoding_failed");
                await stream.WriteAsync(buffer.AsMemory(0, written), ct).ConfigureAwait(false);
            }
            await stream.WriteAsync(Suffix, ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
