using System.Security.Cryptography;
using System.Text.Json;

namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// AES-256-GCM token-at-rest for the client. The file blob is ciphertext
/// (nonce | tag | cipher); it cannot be read as plaintext. POC caveat: the data key sits in a
/// sibling file on the same host — good enough to demonstrate encryption at rest, but a real build
/// uses the OS secret store (v1.x) or a passphrase-derived key.
/// </summary>
public sealed class EncryptedFileTokenStore : IClientTokenStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly Lock KeyCreationGate = new();

    private readonly string _tokenPath;
    private readonly string _keyPath;

    public EncryptedFileTokenStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _tokenPath = Path.Combine(dataDirectory, "esi-tokens.bin");
        _keyPath = Path.Combine(dataDirectory, "esi-tokens.key");
    }

    public async Task SaveAsync(EsiTokenSet tokens, CancellationToken cancellationToken = default)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(tokens);
        var key = GetOrCreateKey();

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plaintext, cipher, tag);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize + TagSize, cipher.Length);

        // Write-then-rename so a concurrent reader never sees a half-written blob (File.Move is atomic on the volume).
        // A unique temp name per save keeps two concurrent saves from colliding on one .tmp.
        var tempPath = $"{_tokenPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, blob, cancellationToken);
            File.Move(tempPath, _tokenPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch (IOException) { }
            throw;
        }
    }

    public async Task<EsiTokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_tokenPath) || !File.Exists(_keyPath))
            return null;

        var blob = await File.ReadAllBytesAsync(_tokenPath, cancellationToken);
        if (blob.Length < NonceSize + TagSize)
            return null;

        var key = GetOrCreateKey();
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[cipher.Length];

        try
        {
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, cipher, tag, plaintext);

            return JsonSerializer.Deserialize<EsiTokenSet>(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // A corrupt/tampered blob or a key mismatch (tag-mismatch) is a cache-miss, not a fatal error — returning
            // null lets the caller fall back to a fresh re-auth instead of crashing the token-refresh flow.
            return null;
        }
    }

    private byte[] GetOrCreateKey()
    {
        if (File.Exists(_keyPath))
            return File.ReadAllBytes(_keyPath);

        // Serialize key creation: two concurrent SaveAsync calls must not each generate and write their own key, or
        // the second would clobber the first and make the already-encrypted blob undecryptable (tag-mismatch).
        lock (KeyCreationGate)
        {
            if (File.Exists(_keyPath))
                return File.ReadAllBytes(_keyPath);

            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                // CreateNew guards the cross-process race too: a second process loses the create and re-reads the
                // winner's key below instead of overwriting it.
                using (var stream = new FileStream(_keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(key);
                TryRestrictPermissions(_keyPath);
                return key;
            }
            catch (IOException)
            {
                return File.ReadAllBytes(_keyPath);
            }
        }
    }

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (IOException)
            {
                // Best-effort hardening; ignore on filesystems that don't support it.
            }
        }
    }
}
