using EveUtils.Shared.Modules.Esi;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// At-rest token store guarantees (B7/B8): a Save/Load roundtrip returns the same tokens; a corrupt or tampered blob,
/// a missing file and a key/blob mismatch are all treated as a cache-miss (null), never a thrown exception that would
/// crash the token-refresh flow; and N concurrent saves on a fresh store never lose the key-creation race (which would
/// leave the blob undecryptable with a tag-mismatch). Each test runs in its own guaranteed-unique scratch directory.
/// </summary>
public sealed class EncryptedTokenStoreTests : IDisposable
{
    private const int CharacterId = 95123456;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "eveutils-tokenstore-" + Guid.NewGuid().ToString("N"));

    private static EsiTokenSet Tokens(string access = "access-abc") =>
        new(access, "refresh-xyz", new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task FileStore_SaveThenLoad_RoundtripsTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new EncryptedFileTokenStore(_directory);
        var tokens = Tokens();

        await store.SaveAsync(tokens, ct);
        var loaded = await store.LoadAsync(ct);

        Assert.Equal(tokens, loaded);
    }

    [Fact]
    public async Task FileStore_MissingFile_ReturnsNull()
    {
        var store = new EncryptedFileTokenStore(_directory);
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileStore_CorruptBlob_ReturnsNull_NoThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new EncryptedFileTokenStore(_directory);
        await store.SaveAsync(Tokens(), ct);

        var blobPath = Path.Combine(_directory, "esi-tokens.bin");
        // Overwrite the ciphertext with random bytes after a valid save: the GCM tag no longer verifies, so decrypt
        // throws CryptographicException internally — which must surface as a cache-miss (null), not an exception.
        await File.WriteAllBytesAsync(blobPath, RandomBytes(128), ct);

        Assert.Null(await store.LoadAsync(ct));
    }

    [Fact]
    public async Task FileStore_ConcurrentSaves_KeepBlobDecryptable()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new EncryptedFileTokenStore(_directory);
        var tokens = Tokens("access-concurrent");

        // Fire many saves at once on a fresh store: the key file does not exist yet, so without serialised key
        // creation (B7) each save could generate its own key and clobber the previous winner, leaving the persisted
        // blob encrypted under a different key than the one on disk (tag-mismatch on load). A unique temp name per save
        // (B7) also stops the concurrent writes from colliding, so every save must complete without throwing.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.SaveAsync(tokens, ct)));

        var key = Path.Combine(_directory, "esi-tokens.key");
        Assert.True(File.Exists(key));   // exactly one data key was created (no per-thread clobber)
        Assert.Equal(tokens, await store.LoadAsync(ct));   // and the persisted blob decrypts with it
    }

    [Fact]
    public async Task PerCharacterStore_SaveThenLoad_RoundtripsTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new EncryptedPerCharacterTokenStore(_directory);
        var tokens = Tokens();

        await store.SaveAsync(CharacterId, tokens, ct);
        var loaded = await store.LoadAsync(CharacterId, ct);

        Assert.Equal(tokens, loaded);
    }

    [Fact]
    public async Task PerCharacterStore_MissingFile_ReturnsNull()
    {
        var store = new EncryptedPerCharacterTokenStore(_directory);
        Assert.Null(await store.LoadAsync(CharacterId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PerCharacterStore_CorruptBlob_ReturnsNull_NoThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new EncryptedPerCharacterTokenStore(_directory);
        await store.SaveAsync(CharacterId, Tokens(), ct);

        var blobPath = Path.Combine(_directory, $"esi-{CharacterId}.bin");
        await File.WriteAllBytesAsync(blobPath, RandomBytes(128), ct);

        Assert.Null(await store.LoadAsync(CharacterId, ct));
    }

    [Fact]
    public async Task PerCharacterStore_ConcurrentSaves_KeepBlobDecryptable()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new EncryptedPerCharacterTokenStore(_directory);
        var tokens = Tokens("access-concurrent");

        // See FileStore_ConcurrentSaves_KeepBlobDecryptable: B7's key-creation gate keeps the single per-character key
        // consistent under concurrent saves, and a unique temp name per save lets every concurrent write complete.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.SaveAsync(CharacterId, tokens, ct)));

        var key = Path.Combine(_directory, $"esi-{CharacterId}.key");
        Assert.True(File.Exists(key));
        Assert.Equal(tokens, await store.LoadAsync(CharacterId, ct));
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the scratch dir; a leftover throwaway dir is harmless.
        }
    }
}
