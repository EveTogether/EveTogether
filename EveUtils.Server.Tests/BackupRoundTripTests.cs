using System.Security.Cryptography;
using System.Text.Json;
using EveUtils.Server.Auth;
using EveUtils.Server.Backup;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Backup.Entities;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Services;
using EveUtils.Shared.Modules.ServerAuth.Services.Implementations;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// Export and restore end to end against a real migrated database (ET-99): take an archive, destroy the server,
/// put it back, and check that what comes back is what went in — keys included, because a paired character is
/// referenced by its id from its sessions and its roster entries.
/// </summary>
public class BackupRoundTripTests : IDisposable
{
    private const string Password = "restore-me-please-1234";

    private readonly MigratedSqliteServerDatabase _database = new();
    private readonly ServerBackupOptions _options;
    private readonly BackupExporter _exporter;
    private readonly BackupRestorer _restorer;
    private readonly byte[] _tokenProtectorKey = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _certificate = RandomNumberGenerator.GetBytes(512);

    public BackupRoundTripTests()
    {
        _options = new ServerBackupOptions(_database.DataDirectory);
        _exporter = new BackupExporter(_database, new ServerInfo("Test server"), _options);
        _restorer = new BackupRestorer(_database, _options, _exporter, NullLogger<BackupRestorer>.Instance);

        File.WriteAllBytes(KeyPath, _tokenProtectorKey);
        File.WriteAllBytes(CertificatePath, _certificate);
    }

    public void Dispose() => _database.Dispose();

    private string KeyPath => Path.Combine(_database.DataDirectory, BackupFormat.TokenProtectorKeyFile);
    private string CertificatePath => Path.Combine(_database.DataDirectory, BackupFormat.ServerCertificateFile);

    [Fact]
    public async Task Restore_AfterEverythingWasDeleted_BringsBackTheRowsAndTheIdentityFiles()
    {
        await SeedAsync();
        var archive = await ExportAsync();

        await DeleteEverythingAsync();
        File.WriteAllBytes(KeyPath, RandomNumberGenerator.GetBytes(32));

        var result = await _restorer.RestoreAsync(new MemoryStream(archive), Password, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Messages.Select(m => m.Text)));
        await AssertSeededStateIsBackAsync();
        Assert.Equal(_tokenProtectorKey, File.ReadAllBytes(KeyPath));
        Assert.Equal(_certificate, File.ReadAllBytes(CertificatePath));
        Assert.True(result.Value?.TokenProtectorKeyRestored);
    }

    /// <summary>
    /// What the operator asked for (ET-102): the file a download produces is an ordinary ZIP. It carries the local
    /// file header every tool looks for, its entry names list without the password — the way 7-Zip shows you the
    /// contents before it asks for one — and every entry is AES-256, not the ZipCrypto that Explorer would have
    /// opened and anyone else would have broken in an afternoon.
    /// </summary>
    [Fact]
    public async Task Export_ProducesAnAes256ZipThatListsWithoutThePassword()
    {
        await SeedAsync();

        var archive = await ExportAsync();

        Assert.Equal<byte[]>([0x50, 0x4B, 0x03, 0x04], archive[..4]);

        using var zip = new ZipFile(new MemoryStream(archive));
        var entries = zip.Cast<ZipEntry>().ToList();
        Assert.Contains(entries, e => e.Name == BackupFormat.ManifestEntry);
        Assert.All(entries, e => Assert.Equal(BackupZip.KeySize, e.AESKeySize));
        Assert.Throws<ZipException>(() => zip.GetInputStream(entries[0]));
    }

    /// <summary>The owned <c>AssignedFit</c> is table-split onto the member row — the case a provider-neutral
    /// export loses if it walks entities instead of columns.</summary>
    [Fact]
    public async Task Restore_OwnedFitSnapshotOnAFleetMember_ComesBackWhole()
    {
        await SeedAsync();
        var archive = await ExportAsync();
        await DeleteEverythingAsync();

        await _restorer.RestoreAsync(new MemoryStream(archive), Password, TestContext.Current.CancellationToken);

        await using var db = _database.CreateDbContext();
        var member = await db.Set<FleetMember>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(member.AssignedFit);
        Assert.Equal("Hurricane — shield buffer", member.AssignedFit.FitName);
        Assert.Equal("c0ffee", member.AssignedFit.ContentHash);
        Assert.Equal(24698, member.AssignedFit.ShipTypeId);
    }

    /// <summary>A restore keeps the original keys, and the identity counter has to move past them — otherwise the
    /// first row written after the restore collides with one that is already there.</summary>
    [Fact]
    public async Task Restore_ThenInsertingANewRow_DoesNotCollideWithARestoredKey()
    {
        await SeedAsync();
        var archive = await ExportAsync();
        await DeleteEverythingAsync();
        await _restorer.RestoreAsync(new MemoryStream(archive), Password, TestContext.Current.CancellationToken);

        await using var db = _database.CreateDbContext();
        db.Add(new SyncedCharacter { EsiCharacterId = 91000002, CharacterName = "Later", GrantedScopesJson = "[]" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await db.Set<SyncedCharacter>().CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restore_WrongPassword_FailsAndLeavesTheServerAlone()
    {
        await SeedAsync();
        var archive = await ExportAsync();

        var result = await _restorer.RestoreAsync(new MemoryStream(archive), "the-wrong-passphrase", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageCodes.BackupPasswordWrong, result.Messages[0].Code);
        await AssertSeededStateIsBackAsync();
    }

    [Fact]
    public async Task Restore_TruncatedArchive_FailsBeforeDroppingAnything()
    {
        await SeedAsync();
        var archive = await ExportAsync();

        var result = await _restorer.RestoreAsync(new MemoryStream(archive[..(archive.Length / 2)]), Password, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageCodes.BackupCorrupt, result.Messages[0].Code);
        await AssertSeededStateIsBackAsync();
    }

    /// <summary>An archive that decrypts cleanly but whose contents no longer match the manifest still has to be
    /// refused, and refused before the database is dropped.</summary>
    [Fact]
    public async Task Restore_EntryEditedAfterTheManifestWasWritten_FailsBeforeDroppingAnything()
    {
        await SeedAsync();
        var archive = await ExportAsync();

        var result = await _restorer.RestoreAsync(new MemoryStream(EditATableEntry(archive)), Password, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageCodes.BackupCorrupt, result.Messages[0].Code);
        Assert.Contains("checksum", result.Messages[0].Text, StringComparison.OrdinalIgnoreCase);
        await AssertSeededStateIsBackAsync();
    }

    /// <summary>The assistant's condition on this ticket: the state from just before the drop is archived, under
    /// the same password, so a restore that dies halfway is not the end of the server.</summary>
    [Fact]
    public async Task Restore_Always_LeavesAReadableArchiveOfWhatItReplaced()
    {
        await SeedAsync();
        var archive = await ExportAsync();
        await DeleteEverythingAsync();
        await SeedAsync(characterName: "State before the restore");

        var result = await _restorer.RestoreAsync(new MemoryStream(archive), Password, TestContext.Current.CancellationToken);

        var safety = result.Value?.SafetyArchivePath;
        Assert.NotNull(safety);
        Assert.True(File.Exists(safety));

        await using var safetyStream = File.OpenRead(safety);
        var restoredAgain = await _restorer.RestoreAsync(safetyStream, Password, TestContext.Current.CancellationToken);

        Assert.True(restoredAgain.IsSuccess, string.Join("; ", restoredAgain.Messages.Select(m => m.Text)));
        await using var db = _database.CreateDbContext();
        var character = await db.Set<SyncedCharacter>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("State before the restore", character.CharacterName);
    }

    /// <summary>
    /// A restore that failed and is retried straight away lands in the same second as the previous one. The earlier
    /// safety archive can be the last surviving copy of a token-protector key, so the second restore steps around
    /// it instead of writing over it.
    /// </summary>
    [Fact]
    public async Task Restore_WhenASafetyArchiveFromTheSameSecondExists_StepsAroundItInsteadOfOverwriting()
    {
        await SeedAsync();
        var archive = await ExportAsync();

        // Every name the next few seconds could produce, so the collision path runs whichever second this lands in.
        var occupied = Enumerable.Range(0, 5)
            .Select(second => Path.Combine(_database.DataDirectory,
                BackupFormat.PreRestoreFileName(DateTimeOffset.UtcNow.AddSeconds(second))))
            .ToList();
        foreach (var path in occupied)
            File.WriteAllText(path, "an earlier safety archive");

        var result = await _restorer.RestoreAsync(new MemoryStream(archive), Password, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Messages.Select(m => m.Text)));
        Assert.EndsWith("-2" + BackupFormat.FileExtension, result.Value?.SafetyArchivePath);
        foreach (var path in occupied)
            Assert.Equal("an earlier safety archive", File.ReadAllText(path));
    }

    /// <summary>
    /// ET-94, forwards: a restored key was not generated during this start, so the new-identity guard has nothing
    /// to fire on — and the refresh token in the restored database is readable again with it.
    /// </summary>
    [Fact]
    public async Task Restore_ThenReadingTheKeyBack_DoesNotLookLikeANewIdentity()
    {
        await SeedAsync();
        var archive = await ExportAsync();
        await DeleteEverythingAsync();
        File.Delete(KeyPath);

        await _restorer.RestoreAsync(new MemoryStream(archive), Password, TestContext.Current.CancellationToken);

        var protector = new AesGcmTokenProtector(_database.DataDirectory);
        await using var db = _database.CreateDbContext();
        var character = await db.Set<SyncedCharacter>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        Assert.False(protector.KeyWasCreated);
        Assert.False(NewIdentityGuard.ShouldRefuseStart(protector.KeyWasCreated, syncedCharacterCount: 1, newIdentityAccepted: false));
        Assert.Equal("the-refresh-token", protector.Unprotect(new EncryptedToken(
            character.RefreshTokenCipher, character.RefreshTokenNonce, character.RefreshTokenTag)));
    }

    /// <summary>
    /// ET-94, backwards: a database restored without its key is exactly the situation the guard exists for. The key
    /// is generated on the next start, the paired characters are there, and the server refuses.
    /// </summary>
    [Fact]
    public async Task Restore_DatabaseWithoutTheKey_RunsIntoTheNewIdentityGuard()
    {
        await SeedAsync();
        var archive = await ExportAsync();
        await DeleteEverythingAsync();

        var result = await _restorer.RestoreAsync(new MemoryStream(WithoutTheTokenProtectorKey(archive)), Password, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, string.Join("; ", result.Messages.Select(m => m.Text)));
        Assert.False(result.Value?.TokenProtectorKeyRestored);

        File.Delete(KeyPath);
        var protector = new AesGcmTokenProtector(_database.DataDirectory);
        await using var db = _database.CreateDbContext();
        var paired = await db.Set<SyncedCharacter>().CountAsync(TestContext.Current.CancellationToken);

        Assert.True(protector.KeyWasCreated);
        Assert.True(NewIdentityGuard.ShouldRefuseStart(protector.KeyWasCreated, paired, newIdentityAccepted: false));
    }

    private async Task SeedAsync(string characterName = "Jithran")
    {
        await using var db = _database.CreateDbContext();
        var protector = new AesGcmTokenProtector(_database.DataDirectory);
        var token = protector.Protect("the-refresh-token");

        var character = new SyncedCharacter
        {
            EsiCharacterId = 91000000,
            CharacterName = characterName,
            RefreshTokenCipher = token.Cipher,
            RefreshTokenNonce = token.Nonce,
            RefreshTokenTag = token.Tag,
            PairedAt = new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero),
            GrantedScopesJson = """["esi-fleets.read_fleet.v1"]""",
        };
        db.Add(character);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Deliberately a session, not just a character: ServerSession has a foreign key to SyncedCharacter and
        // sorts before it alphabetically, so any insert order that is not the foreign-key order fails here.
        db.Add(new ServerSession
        {
            SyncedCharacterId = character.Id,
            AccessTokenHash = "access-hash",
            RefreshTokenHash = "refresh-hash",
            IssuedAt = new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero),
            RefreshExpiresAt = new DateTimeOffset(2027, 8, 30, 8, 0, 0, TimeSpan.Zero),
            LastHeartbeat = new DateTimeOffset(2026, 8, 30, 8, 30, 0, TimeSpan.Zero),
        });

        var fleet = new Fleet
        {
            Name = "Home defence",
            CreatorCharacterId = 91000000,
            CreatedAt = new DateTimeOffset(2026, 8, 1, 20, 0, 0, TimeSpan.Zero),
            LastActivityAt = new DateTimeOffset(2026, 8, 1, 21, 30, 0, TimeSpan.Zero),
        };
        db.Add(fleet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Add(new FleetMember
        {
            FleetId = fleet.Id,
            CharacterId = 91000000,
            JoinTime = new DateTimeOffset(2026, 8, 1, 20, 5, 0, TimeSpan.Zero),
            AssignedFit = new FitReference
            {
                ShipTypeId = 24698,
                FitName = "Hurricane — shield buffer",
                RawJson = """{"name":"Hurricane"}""",
                ContentHash = "c0ffee",
            },
        });
        db.Add(new BackupDownload
        {
            AdminUserId = 1,
            AdminUsername = "admin",
            DownloadedAt = new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero),
            AppVersion = "0.2.0",
            FileName = "eve-together-backup-20260830-090000Z.zip",
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<byte[]> ExportAsync()
    {
        var buffer = new MemoryStream();
        await _exporter.WriteAsync(buffer, Password, TestContext.Current.CancellationToken);
        return buffer.ToArray();
    }

    private async Task DeleteEverythingAsync()
    {
        await using var db = _database.CreateDbContext();
        db.RemoveRange(await db.Set<FleetMember>().ToListAsync(TestContext.Current.CancellationToken));
        db.RemoveRange(await db.Set<Fleet>().ToListAsync(TestContext.Current.CancellationToken));
        db.RemoveRange(await db.Set<ServerSession>().ToListAsync(TestContext.Current.CancellationToken));
        db.RemoveRange(await db.Set<SyncedCharacter>().ToListAsync(TestContext.Current.CancellationToken));
        db.RemoveRange(await db.Set<BackupDownload>().ToListAsync(TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task AssertSeededStateIsBackAsync()
    {
        await using var db = _database.CreateDbContext();
        var character = await db.Set<SyncedCharacter>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(91000000, character.EsiCharacterId);
        Assert.Equal(new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero), character.PairedAt);
        Assert.Equal(["esi-fleets.read_fleet.v1"], character.GrantedScopes);

        var member = await db.Set<FleetMember>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var fleet = await db.Set<Fleet>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(fleet.Id, member.FleetId);

        // The session still points at the character it belonged to — the keys came back, not just the rows.
        var session = await db.Set<ServerSession>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(character.Id, session.SyncedCharacterId);

        var download = await db.Set<BackupDownload>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("admin", download.AdminUsername);
    }

    /// <summary>Rewrites one table entry inside the archive, leaving the manifest — and therefore its checksum for
    /// that entry — as it was.</summary>
    private static byte[] EditATableEntry(byte[] archive) =>
        RewriteZip(archive, entries =>
        {
            var index = entries.FindIndex(e =>
                e.Name.StartsWith(BackupFormat.DatabaseEntryPrefix, StringComparison.Ordinal) && e.Content.Length > 0);
            entries[index] = entries[index] with { Content = "not what the manifest says"u8.ToArray() };
        });

    private static byte[] WithoutTheTokenProtectorKey(byte[] archive) =>
        RewriteZip(archive, entries =>
        {
            entries.RemoveAll(e => e.Name == BackupFormat.DataEntryPrefix + BackupFormat.TokenProtectorKeyFile);

            // The manifest has to stop listing it too, or the checksum pass reports a missing entry instead.
            var index = entries.FindIndex(e => e.Name == BackupFormat.ManifestEntry);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(entries[index].Content, BackupJson.Options)
                ?? throw new InvalidOperationException("The archive has no readable manifest.");

            manifest.Files.RemoveAll(f => f.Name == BackupFormat.TokenProtectorKeyFile);
            entries[index] = entries[index] with
            {
                Content = JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJson.Options),
            };
        });

    /// <summary>Unpacks the archive, hands the entries to <paramref name="edit"/> and writes them out again under
    /// the same password. Rebuilding rather than patching in place keeps the result a normal archive: every entry
    /// gets its own salt and authentication code, exactly as the exporter would have written it.</summary>
    private static byte[] RewriteZip(byte[] archive, Action<List<ArchiveEntry>> edit)
    {
        var entries = new List<ArchiveEntry>();
        using (var source = BackupZip.OpenReader(new MemoryStream(archive), Password))
        {
            foreach (var entry in source.Cast<ZipEntry>())
            {
                var content = new MemoryStream();
                using (var stream = source.GetInputStream(entry))
                    stream.CopyTo(content);

                entries.Add(new ArchiveEntry(entry.Name, content.ToArray()));
            }
        }

        edit(entries);

        var rebuilt = new MemoryStream();
        using (var zip = BackupZip.CreateWriter(rebuilt, Password))
        {
            foreach (var entry in entries)
            {
                zip.PutNextEntry(BackupZip.Entry(entry.Name, DateTimeOffset.UnixEpoch));
                zip.Write(entry.Content);
                zip.CloseEntry();
            }
        }

        return rebuilt.ToArray();
    }

    private sealed record ArchiveEntry(string Name, byte[] Content);
}
