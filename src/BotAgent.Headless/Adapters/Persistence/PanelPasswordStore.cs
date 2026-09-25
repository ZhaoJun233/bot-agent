using System.Security.Cryptography;

namespace BotAgent.Adapters.Persistence;

internal sealed class PanelPasswordStore
{
    private const string InitialPasswordEnvironmentVariable = "QQCHAT_PANEL_PASSWORD";
    private const int Iterations = 210_000;
    private readonly SecretsStore _secrets = new();
    private readonly object _gate = new();
    private byte[] _salt;
    private byte[] _hash;

    public bool MustChange { get; private set; }
    public bool Created { get; }

    public PanelPasswordStore(string path)
    {
        var stored = _secrets.Load("panelPassword", throwOnError: true);
        var legacy = SecretFiles.TryRead(path, out var readError);
        if (readError is not null)
            throw new IOException("Cannot read panel password file: " + readError);
        if (stored is not null || legacy is not null)
        {
            var parts = (stored ?? legacy!).Split(':');
            if (parts.Length != 3 || (parts[0] != "0" && parts[0] != "1"))
                throw new InvalidDataException("Invalid panel password file");
            MustChange = parts[0] == "0";
            _salt = Convert.FromBase64String(parts[1]);
            _hash = Convert.FromBase64String(parts[2]);
            if (_salt.Length != 16 || _hash.Length != 32)
                throw new InvalidDataException("Invalid panel password hash");
            if (stored is null)
                _secrets.Save("panelPassword", legacy, throwOnError: true);
            if (legacy is not null)
                File.Delete(path);
        }
        else
        {
            var initialPassword = Environment.GetEnvironmentVariable(InitialPasswordEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(initialPassword) || initialPassword.Length < 10 || initialPassword.Length > 200)
            {
                throw new InvalidOperationException(
                    $"首次启动必须设置 {InitialPasswordEnvironmentVariable}（10-200 个字符）；初始化后可从环境变量移除。");
            }

            _salt = RandomNumberGenerator.GetBytes(16);
            _hash = Hash(initialPassword, _salt);
            MustChange = true;
            _secrets.Save("panelPassword", Serialize(), throwOnError: true);
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
            _secrets.Save("panelPassword", content, throwOnError: true);
            _salt = salt;
            _hash = hash;
            MustChange = false;
        }
    }

    private string Serialize() => $"{(MustChange ? 0 : 1)}:{Convert.ToBase64String(_salt)}:{Convert.ToBase64String(_hash)}";

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

}
