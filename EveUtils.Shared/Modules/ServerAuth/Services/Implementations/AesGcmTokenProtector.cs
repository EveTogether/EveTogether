using System.Security.Cryptography;
using System.Text;
using EveUtils.Shared.Modules.ServerAuth.Services;

namespace EveUtils.Shared.Modules.ServerAuth.Services.Implementations;

/// <summary>
/// AES-256-GCM token protector. The data key is a random 256-bit key persisted in the server
/// data folder. POC caveat: a real build derives/wraps this with a KMS or an admin
/// passphrase-KDF (envelope KEK/DEK) so the key isn't sibling to the database — not resolved here.
/// </summary>
internal sealed class AesGcmTokenProtector : ITokenProtector
{
    private const int TagSize = 16;
    private const int NonceSize = 12;

    private readonly byte[] _key;

    public AesGcmTokenProtector(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var keyPath = Path.Combine(dataDirectory, "token-protector.key");
        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, _key);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try { File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch (IOException) { }
            }
        }
    }

    public EncryptedToken Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, cipher, tag);
        return new EncryptedToken(cipher, nonce, tag);
    }

    public string Unprotect(EncryptedToken token)
    {
        var plaintext = new byte[token.Cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(token.Nonce, token.Cipher, token.Tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
