using System.Text;
using System.Text.RegularExpressions;

namespace BotAgent.Adapters.Plugins;

/// <summary>Enforces the declared source/passive-data stdlib-only package shape, not a sandbox.</summary>
internal static class PluginPythonArchive
{
    private static readonly HashSet<string> ForbiddenDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".venv", "venv", "site-packages", "__pycache__" };
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".py", ".json", ".txt", ".md" };
    private static readonly HashSet<string> ForbiddenFiles = new(StringComparer.OrdinalIgnoreCase)
        { "requirements.txt", "pyproject.toml", "setup.py", "setup.cfg", "pipfile", "poetry.lock" };
    internal static void ValidatePath(string name, int unixType, bool directory)
    {
        var parts = name.Split('/');
        if (parts.Any(part => part.EndsWith('.') || part.EndsWith(' ') ||
            Regex.IsMatch(part.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase)) ||
            parts.Any(ForbiddenDirectories.Contains) || !directory && !AllowedExtensions.Contains(Path.GetExtension(name)) ||
            ForbiddenFiles.Contains(parts[^1]) || unixType is not (0 or 0x8000 or 0x4000) ||
            unixType == 0x4000 && !directory || unixType == 0x8000 && directory)
            throw new InvalidDataException("Python stdlib-only archive shape is invalid.");
    }
    internal static async Task ValidateSourceAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        try { await reader.ReadToEndAsync(ct).ConfigureAwait(false); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Python sources must be UTF-8."); }
    }
}
