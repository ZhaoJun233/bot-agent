using BotAgent.Services.Voice;

namespace BotAgent.Packages.Voice;

/// <summary>
/// Package-owned WAV (PCM) and MP3 sample concatenator for MiniMax voice-clone multi-clip uploads.
/// Moved out of <c>src/BotAgent.Headless/Services/Voice/VoiceService.cs</c> into <c>preset.feature.voice</c>.
/// </summary>
public static class VoiceSampleConcatenator
{
    public static VoiceConcatOutcome Concat(VoiceSampleInput[] samples)
    {
        if (samples is null || samples.Length == 0)
        {
            return new(null, "没有样本", string.Empty);
        }

        if (samples.Length == 1)
        {
            return new(samples[0].Data, null, string.Empty);
        }

        var allWav = samples.All(s => IsWav(s.Data));
        var allMp3 = samples.All(s => IsMp3(s.Data));
        if (allWav)
        {
            return ConcatWav(samples);
        }

        if (allMp3)
        {
            return ConcatMp3(samples);
        }

        if (samples.Any(s => IsM4a(s.Data)) || samples.Any(s => !IsWav(s.Data) && !IsMp3(s.Data)))
        {
            return new(null, "这些切片里有 mm4a/其它格式 —— 面板只能拼 **mp3 与 wav**（都是 mp3 或都是 wav）；" +
                             "m4a 请先在本地导出成 mp3 再传（手机录音默认就是 m4a，很多播放器能直接转）", string.Empty);
        }

        return new(null, "样本格式不一致：一部分是 wav、一部分是 mp3 —— 全部统一成同一种再传", string.Empty);
    }

    private static bool IsWav(byte[] d) => d.Length > 44 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F'
                                           && d[8] == 'W' && d[9] == 'A' && d[10] == 'V' && d[11] == 'E';

    private static bool IsMp3(byte[] d) => d.Length > 4
                                           && ((d[0] == 'I' && d[1] == 'D' && d[2] == '3') || (d[0] == 0xFF && (d[1] & 0xE0) == 0xE0));

    private static bool IsM4a(byte[] d) => d.Length > 12 && d[4] == 'f' && d[5] == 't' && d[6] == 'y' && d[7] == 'p';

    private static VoiceConcatOutcome ConcatWav(VoiceSampleInput[] samples)
    {
        ushort channels = 0;
        uint rate = 0;
        ushort bits = 0;
        var pcm = new List<byte[]>();

        foreach (var sample in samples)
        {
            var (ok, ch, rt, bt, chunk) = ReadWav(sample.Data);
            if (!ok)
            {
                return new(null, $"读不了这段 wav（{sample.Name}）—— 可能是非标准头部（手机导出/剪辑软件有时会写额外块）", string.Empty);
            }

            if (channels == 0)
            {
                (channels, rate, bits) = (ch, rt, bt);
            }
            else if (ch != channels || rt != rate || bt != bits)
            {
                return new(null, $"这些 wav 的参数不一致（{sample.Name} 是 {rt}Hz/{ch}声道/{bt}bit，前面的是 {rate}Hz/{channels}声道/{bits}bit）—— " +
                                 "请先统一成同一采样率再拼", string.Empty);
            }

            pcm.Add(chunk);
        }

        const long maxOutputBytes = 20L * 1024 * 1024;
        var bytesPerFrame = Math.Max(1, channels * (bits / 8));
        var gapBytes = (long)bytesPerFrame * (long)(rate * 0.3);
        long totalLong = gapBytes * (pcm.Count - 1);
        foreach (var p in pcm) totalLong += p.Length;
        if (totalLong < 0 || 44L + totalLong > maxOutputBytes)
        {
            return new(null, "拼接后的 wav 超过 20MB 上限，请减少切片或缩短时长", string.Empty);
        }
        var gap = new byte[(int)gapBytes];
        var total = (int)totalLong;
        var outBuf = new byte[44 + total];
        WriteWavHeader(outBuf, channels, rate, bits, total);
        var pos = 44;
        for (var i = 0; i < pcm.Count; i++)
        {
            if (i > 0)
            {
                Array.Copy(gap, 0, outBuf, pos, gap.Length);
                pos += gap.Length;
            }

            Array.Copy(pcm[i], 0, outBuf, pos, pcm[i].Length);
            pos += pcm[i].Length;
        }

        var seconds = total / (double)bytesPerFrame / rate;
        return new(outBuf, null, $"把 {samples.Length} 段 wav 拼成了一段（共 {seconds:F1} 秒，段间 300ms 静音）");
    }

    private static (bool Ok, ushort Channels, uint Rate, ushort Bits, byte[] Pcm) ReadWav(byte[] d)
    {
        if (!IsWav(d))
        {
            return (false, 0, 0, 0, Array.Empty<byte>());
        }

        ushort channels = 0;
        uint rate = 0;
        ushort bits = 0;
        var pos = 12;
        while (pos + 8 <= d.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(d, pos, 4);
            var size = BitConverter.ToInt32(d, pos + 4);
            var body = pos + 8;
            if (size < 0 || size > d.Length - body)
            {
                break;
            }

            if (id == "fmt ")
            {
                if (size < 16)
                {
                    return (false, 0, 0, 0, Array.Empty<byte>());
                }
                var audioFormat = BitConverter.ToUInt16(d, body);
                var ch = BitConverter.ToUInt16(d, body + 2);
                var rt = BitConverter.ToUInt32(d, body + 4);
                var bt = BitConverter.ToUInt16(d, body + 14);
                if (audioFormat != 1 || ch is < 1 or > 8 || rt is < 8000 or > 192000 || bt is not (8 or 16 or 24 or 32))
                {
                    return (false, 0, 0, 0, Array.Empty<byte>());
                }
                channels = ch;
                rate = rt;
                bits = bt;
            }
            else if (id == "data")
            {
                if (channels == 0 || rate == 0 || bits == 0)
                {
                    return (false, 0, 0, 0, Array.Empty<byte>());
                }
                var pcm = new byte[size];
                Array.Copy(d, body, pcm, 0, size);
                return (true, channels, rate, bits, pcm);
            }

            var pad = size % 2;
            if (size > d.Length - body - pad) break;
            pos = body + size + pad;
        }

        return (false, 0, 0, 0, Array.Empty<byte>());
    }

    private static void WriteWavHeader(byte[] buf, ushort channels, uint rate, ushort bits, int dataLen)
    {
        var byteRate = rate * channels * (bits / 8u);
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(buf, 0);
        BitConverter.GetBytes(36 + dataLen).CopyTo(buf, 4);
        System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(buf, 8);
        BitConverter.GetBytes(16).CopyTo(buf, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(buf, 20);
        BitConverter.GetBytes(channels).CopyTo(buf, 22);
        BitConverter.GetBytes(rate).CopyTo(buf, 24);
        BitConverter.GetBytes(byteRate).CopyTo(buf, 28);
        BitConverter.GetBytes((ushort)(channels * (bits / 8))).CopyTo(buf, 32);
        BitConverter.GetBytes(bits).CopyTo(buf, 34);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(buf, 36);
        BitConverter.GetBytes(dataLen).CopyTo(buf, 40);
    }

    private static VoiceConcatOutcome ConcatMp3(VoiceSampleInput[] samples)
    {
        var parts = new List<byte[]>();
        (int Rate, int Channels)? head = null;

        foreach (var sample in samples)
        {
            var frames = StripMp3Tags(sample.Data);
            var info = ReadMp3FrameInfo(frames);
            if (info is null)
            {
                return new(null, $"这段 mp3 里没找到有效的音频帧（{sample.Name}）", string.Empty);
            }

            head ??= (info.Value.Rate, info.Value.Channels);
            if (info.Value.Rate != head.Value.Rate || info.Value.Channels != head.Value.Channels)
            {
                return new(null, $"这些 mp3 的采样率/声道不一致（{sample.Name} 是 {info.Value.Rate}Hz/{info.Value.Channels}声道）—— " +
                                 "不同码率可以，采样率/声道不同会在接缝处咔噃，请先统一", string.Empty);
            }

            parts.Add(frames);
        }

        const long maxOutputBytes = 20L * 1024 * 1024;
        long totalLong = 0;
        foreach (var p in parts) totalLong += p.Length;
        if (totalLong > maxOutputBytes)
        {
            return new(null, "拼接后的 mp3 超过 20MB 上限，请减少切片或缩短时长", string.Empty);
        }
        var total = (int)totalLong;
        var outBuf = new byte[total];
        var pos = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, outBuf, pos, p.Length);
            pos += p.Length;
        }

        var approx = head is null ? 0 : total * 8.0 / 128000;
        return new(outBuf, null, $"把 {samples.Length} 段 mp3 按帧拼成了一段（约 {approx:F0} 秒；要求同采样率/声道，码率可以不同）");
    }

    private static byte[] StripMp3Tags(byte[] d)
    {
        var start = 0;
        if (d.Length > 10 && d[0] == 'I' && d[1] == 'D' && d[2] == '3')
        {
            var size = ((d[6] & 0x7F) << 21) | ((d[7] & 0x7F) << 14) | ((d[8] & 0x7F) << 7) | (d[9] & 0x7F);
            start = Math.Min(d.Length, 10 + size);
        }

        var end = d.Length;
        if (end - start > 128 && d[end - 128] == 'T' && d[end - 127] == 'A' && d[end - 126] == 'G')
        {
            end -= 128;
        }

        while (start + 1 < end && !(d[start] == 0xFF && (d[start + 1] & 0xE0) == 0xE0))
        {
            start++;
        }

        var outBuf = new byte[end - start];
        Array.Copy(d, start, outBuf, 0, outBuf.Length);
        return outBuf;
    }

    private static (int Rate, int Channels)? ReadMp3FrameInfo(byte[] d)
    {
        for (var i = 0; i + 4 <= d.Length && i < 8096; i++)
        {
            if (d[i] != 0xFF || (d[i + 1] & 0xE0) != 0xE0)
            {
                continue;
            }

            var version = (d[i + 1] >> 3) & 0x03;
            var rateIdx = (d[i + 2] >> 2) & 0x03;
            var channelMode = (d[i + 3] >> 6) & 0x03;
            if (rateIdx == 3 || version == 1)
            {
                continue;
            }

            var table = version == 3
                ? new[] { 44100, 48000, 32000 }
                : version == 2 ? new[] { 22050, 24000, 16000 } : new[] { 11025, 12000, 8000 };
            return (table[rateIdx], channelMode == 3 ? 1 : 2);
        }

        return null;
    }
}
