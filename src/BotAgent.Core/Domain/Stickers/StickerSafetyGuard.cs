using System;

namespace BotAgent.Domain.Stickers;

/// <summary>
/// 表情包与多模态图片安全校验器（纯领域规则、零 IO）。
/// 负责防路径穿越、文件魔数签名校验、扩展名白名单与载荷大小防御。
/// </summary>
public static class StickerSafetyGuard
{
    public const long DefaultMaxFileBytes = 10 * 1024 * 1024; // 10MB

    /// <summary>
    /// 校验文件名是否安全（防路径穿越、防注入控制字符），并返回安全文件名。
    /// </summary>
    public static bool TrySanitizeFilename(string? rawFilename, out string safeFilename)
    {
        safeFilename = string.Empty;
        if (string.IsNullOrWhiteSpace(rawFilename))
        {
            return false;
        }

        var name = rawFilename.Trim();

        // 拒绝路径穿越标识与绝对路径
        if (name.Contains("..", StringComparison.Ordinal) ||
            name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        // 防栈溢出：文件名长度超过 255 字符坚决拒绝
        if (name.Length > 255)
        {
            return false;
        }

        // 仅保留基础安全字符（字母、数字、点、下划线、短横线）
        Span<char> buffer = stackalloc char[name.Length];
        int writeIdx = 0;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')
            {
                buffer[writeIdx++] = c;
            }
        }

        if (writeIdx == 0)
        {
            return false;
        }

        safeFilename = new string(buffer[..writeIdx]);
        return true;
    }

    /// <summary>
    /// 基于文件头魔数探测合法的图片格式与扩展名（白名单：PNG/JPEG/GIF/WebP）。
    /// </summary>
    public static bool DetectImageFormat(ReadOnlySpan<byte> header, out string extension, out string mimeType)
    {
        extension = string.Empty;
        mimeType = string.Empty;

        if (header.Length < 12)
        {
            return false;
        }

        // 1. PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            extension = "png";
            mimeType = "image/png";
            return true;
        }

        // 2. JPEG: FF D8 FF
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            extension = "jpg";
            mimeType = "image/jpeg";
            return true;
        }

        // 3. GIF: GIF87a 或 GIF89a (47 49 46 38 37 61 或 47 49 46 38 39 61)
        if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38 &&
            (header[4] == 0x37 || header[4] == 0x39) && header[5] == 0x61)
        {
            extension = "gif";
            mimeType = "image/gif";
            return true;
        }

        // 4. WebP: RIFF .... WEBP (52 49 46 46 .... 57 45 42 50)
        if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
            header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
        {
            extension = "webp";
            mimeType = "image/webp";
            return true;
        }

        return false;
    }

    /// <summary>
    /// 完整校验二进制图片载荷（魔数合法性与字节大小约束）。
    /// </summary>
    public static bool ValidatePayload(
        ReadOnlySpan<byte> bytes,
        long maxBytes,
        out string extension,
        out string mimeType,
        out string error)
    {
        extension = string.Empty;
        mimeType = string.Empty;
        error = string.Empty;

        if (bytes.IsEmpty)
        {
            error = "图片载荷为空";
            return false;
        }

        if (bytes.Length > maxBytes)
        {
            error = $"文件大小({bytes.Length}字节)超过最大允许上限({maxBytes}字节)";
            return false;
        }

        if (!DetectImageFormat(bytes, out extension, out mimeType))
        {
            error = "非法文件签名：仅支持 PNG、JPEG、GIF 与 WebP 图片";
            return false;
        }

        return true;
    }
}
