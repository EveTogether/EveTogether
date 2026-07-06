using System.Security.Cryptography;
using System.Text.Json;

namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// AES-256-GCM token-at-rest per character. Each character gets its own pair of files:
/// <c>esi-{charId}.bin</c> (ciphertext) and <c>esi-{charId}.key</c> (data-key, user-readable only).
/// Same blob layout as <c>EncryptedFileTokenStore</c>: nonce | tag | cipher.
/// </summary>
public sealed class EncryptedPerCharacterTokenStore(string dataDirectory) : IPerCharacterTokenStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly Lock KeyCreationGate = new();

    public async Task SaveAsync(int characterId, EsiTokenSet tokens, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dataDirectory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(tokens);
        var key = GetOrCreateKey(characterId);

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
        // A unique temp name per save keeps two concurrent saves for the same character from colliding on one .tmp.
        var tokenPath = TokenPath(characterId);
        var tempPath = $"{tokenPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, blob, cancellationToken);
            File.Move(tempPath, tokenPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public async Task<EsiTokenSet?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        var tokenPath = TokenPath(characterId);
        var keyPath = KeyPath(characterId);
        if (!File.Exists(tokenPath) || !File.Exists(keyPath))
            return null;

        var blob = await File.ReadAllBytesAsync(tokenPath, cancellationToken);
        if (blob.Length < NonceSize + TagSize)
            return null;

        var key = File.ReadAllBytes(keyPath);
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

    public Task<IReadOnlyList<int>> ListCharacterIdsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(dataDirectory))
            return Task.FromResult<IReadOnlyList<int>>([]);

        var ids = new List<int>();
        foreach (var file in Directory.EnumerateFiles(dataDirectory, "esi-*.bin"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var suffix = name["esi-".Length..];
            if (int.TryParse(suffix, out var id))
                ids.Add(id);
        }

        return Task.FromResult<IReadOnlyList<int>>(ids);
    }

    public Task RemoveAsync(int characterId, CancellationToken cancellationToken = default)
    {
        TryDelete(TokenPath(characterId));
        TryDelete(KeyPath(characterId));
        return Task.CompletedTask;
    }

    private byte[] GetOrCreateKey(int characterId)
    {
        var keyPath = KeyPath(characterId);
        if (File.Exists(keyPath))
            return File.ReadAllBytes(keyPath);

        // Serialize key creation: two concurrent SaveAsync calls must not each generate and write their own key, or
        // the second would clobber the first and make the already-encrypted blob undecryptable (tag-mismatch).
        lock (KeyCreationGate)
        {
            // Re-check inside the lock — another thread may have created the key after the check above.
            if (File.Exists(keyPath))
                return File.ReadAllBytes(keyPath);

            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                // CreateNew guards the cross-process race too: a second process loses the create and re-reads the
                // winner's key below instead of overwriting it.
                using (var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(key);
                TryRestrictPermissions(keyPath);
                return key;
            }
            catch (IOException)
            {
                return File.ReadAllBytes(keyPath);
            }
        }
    }

    private string TokenPath(int characterId) => Path.Combine(dataDirectory, $"esi-{characterId}.bin");
    private string KeyPath(int characterId) => Path.Combine(dataDirectory, $"esi-{characterId}.key");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (IOException) { }
        }
    }
}
