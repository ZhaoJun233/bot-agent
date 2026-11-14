namespace BotAgent.Services.Official;

public sealed partial class OfficialBotGateway
{
    private async Task<(byte[]? Bytes, string ReasonCode)> DownloadAudioAsync(Uri url, CancellationToken ct)
    {
        // HttpClient's timeout ends at headers in streaming mode; retain it for the complete body.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_http.Timeout);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log($"official_download status={(int)response.StatusCode} reason=http_rejected");
                return (null, "audio_unavailable");
            }
            if (response.Content.Headers.ContentLength > MaxUploadBytes) return (null, "media_size_invalid");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var remaining = MaxUploadBytes + 1 - (int)output.Length;
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), timeout.Token).ConfigureAwait(false);
                if (count == 0) return output.Length == 0 ? (null, "audio_unavailable") : (output.ToArray(), string.Empty);
                if (output.Length + count > MaxUploadBytes) return (null, "media_size_invalid");
                output.Write(buffer, 0, count);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            Log("official_download reason=timeout");
            return (null, "audio_download_timeout");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            Log("official_download reason=transport_failed");
            return (null, "audio_unavailable");
        }
    }

    private static bool TryPrepareAudioUrl(string url, out Uri uri)
    {
        if (!TryMediaUrl(url, out uri)) return false;
        var query = uri.Query.TrimStart('?');
        if (query.Split('&').Any(part => string.Equals(Uri.UnescapeDataString(part.Split('=', 2)[0]),
            "format", StringComparison.OrdinalIgnoreCase))) return true;
        var builder = new UriBuilder(uri) { Query = query.Length == 0 ? "format=silk" : query + "&format=silk" };
        uri = builder.Uri;
        return uri.AbsoluteUri.Length <= 2048;
    }
}
