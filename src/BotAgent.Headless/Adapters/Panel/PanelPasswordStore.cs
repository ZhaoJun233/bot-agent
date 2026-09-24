using System.Security.Cryptography;

namespace BotAgent.Adapters.Panel;

internal sealed class PanelPasswordStore
{
    private const string DefaultPassword = "adminBot";
    private const int Iterations = 210_000;
    private readonly string _path;
    private readonly object _gate = new();
    private byte[] _salt;
    private byte[] _hash;

    public bool MustChange { get; private set; }
    public bool Created { get; }

    public PanelPasswordStore(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            var parts = File.ReadAllText(path).Split(':');
            if (parts.Length != 3 || (parts[0] != "0" && parts[0] != "1"))
                throw new InvalidDataException("Invalid panel password file");
            MustChange = parts[0] == "0";
            _salt = Convert.FromBase64String(parts[1]);
            _hash = Convert.FromBase64String(parts[2]);
            if (_salt.Length != 16 || _hash.Length != 32)
                throw new InvalidDataException("Invalid panel password hash");
        }
        else
        {
            _salt = RandomNumberGenerator.GetBytes(16);
            _hash = Hash(DefaultPassword, _salt);
            MustChange = true;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
                writer.Write(Serialize());
            RestrictPermissions(path);
            Created = true;
        }
    }

    public bool Verify(string password)
    {
        if (password.Length > 200) return false;
        lock (_gate)
            return CryptographicOperations.FixedTimeEquals(Hash(password, _salt), _hash);
    }

    public void Change(string password)
    {
        lock (_gate)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Hash(password, salt);
            var content = $"1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, content);
                RestrictPermissions(temporary);
                File.Move(temporary, _path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            _salt = salt;
            _hash = hash;
            MustChange = false;
        }
    }

    private string Serialize() => $"{(MustChange ? 0 : 1)}:{Convert.ToBase64String(_salt)}:{Convert.ToBase64String(_hash)}";

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static void RestrictPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
