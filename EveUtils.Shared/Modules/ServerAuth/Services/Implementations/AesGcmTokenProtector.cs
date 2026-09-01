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

    /// <summary>
    /// True when the key file did not exist and a new one was generated here. The server refuses to start on a
    /// generated key while characters are already paired against the previous one (ET-94) — their refresh tokens
    /// would be permanently undecryptable.
    /// </summary>
    public bool KeyWasCreated { get; }

    /// <summary>Where the key lives, so a start that is refused over <see cref="KeyWasCreated"/> can undo it.</summary>
    public string KeyPath { get; }

    public AesGcmTokenProtector(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        KeyPath = Path.Combine(dataDirectory, "token-protector.key");
        if (File.Exists(KeyPath))
        {
            _key = File.ReadAllBytes(KeyPath);
        }
        else
        {
            KeyWasCreated = true;
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(KeyPath, _key);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try { File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
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
