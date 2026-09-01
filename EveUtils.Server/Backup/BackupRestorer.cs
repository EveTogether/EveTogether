using System.Security.Cryptography;
using System.Text.Json;
using EveUtils.Shared.App;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Backup;

// EveUtils.Server.Stream (the DPS hub) shadows System.IO.Stream inside this project.
using Stream = System.IO.Stream;

/// <summary>
/// Puts a server back the way an archive found it. Destructive by design: the database is dropped and rebuilt, and
/// the TLS certificate and token-protector key are overwritten.
///
/// The order is the whole design. Everything that can refuse — the password, the format, the provider, the
/// migration set, every checksum — happens while the running server is still untouched. Only then is a safety
/// archive of the current state written, and only then does anything get dropped. That safety archive is the
/// answer to the one failure that cannot be rolled back: DDL is not transactional on MySQL, so a restore that
/// dies between the drop and the last insert cannot be undone from inside a transaction.
/// </summary>
internal sealed class BackupRestorer(
    IDbContextFactory<ServerDbContext> contextFactory,
    ServerBackupOptions options,
    BackupExporter exporter,
    ILogger<BackupRestorer> logger) : IScopedService
{
    private const int MaxSafetyArchiveAttempts = 100;

    public async Task<Result<BackupRestoreReport>> RestoreAsync(Stream upload, string password, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.DataDirectory);

        // A ZIP is read from its central directory backwards, so the upload has to be seekable. It lands beside the
        // data it is about to replace — same volume, same permissions — still encrypted, and is removed in the
        // finally below on every path, including the ones that throw.
        var staged = Path.Combine(options.DataDirectory, $"restore-{Guid.NewGuid():N}.tmp");
        try
        {
            await _StageAsync(upload, staged, cancellationToken);
            return await _RestoreStagedAsync(staged, password, cancellationToken);
        }
        catch (CryptographicException)
        {
            // Both a wrong password and an edited byte end here, and the message says both: naming which one it
            // was would help someone guessing.
            return Result<BackupRestoreReport>.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.BackupPasswordWrong,
                "The archive could not be decrypted. Either the password is wrong or the file has been altered."));
        }
        catch (InvalidDataException ex)
        {
            return Result<BackupRestoreReport>.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.BackupCorrupt, ex.Message));
        }
        catch (ZipException ex)
        {
            // Reached only after the archive opened and the password was proven on its manifest, so what is left is
            // the file itself: an entry whose AES authentication code no longer matches what it holds.
            return Result<BackupRestoreReport>.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.BackupCorrupt, $"The backup archive is damaged and was not restored: {ex.Message}"));
        }
        catch (IOException ex)
        {
            // Reached only while staging or writing the safety archive — before anything is dropped. The panel gets
            // a message rather than an error page, and the server is still whole.
            logger.LogError(ex, "A backup restore could not use the data directory {DataDirectory}.", options.DataDirectory);
            return Result<BackupRestoreReport>.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.BackupRestoreFailed,
                $"The restore could not write to the server data directory, so nothing was changed: {ex.Message}"));
        }
        finally
        {
            _TryDelete(staged);
        }
    }

    private async Task<Result<BackupRestoreReport>> _RestoreStagedAsync(string staged, string password, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None);
        using var zip = BackupZip.OpenReader(file, password);
        var manifest = _ReadManifest(zip);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var provider = BackupProviderName.Resolve(db.Database.ProviderName);

        var compatibility = BackupCompatibility.Check(
            manifest, [.. db.Database.GetMigrations()], provider, AppInfo.Version);
        if (!compatibility.IsSuccess)
            return Result<BackupRestoreReport>.Failure([.. compatibility.Messages]);

        BackupArchiveVerifier.Verify(zip, manifest);

        var safetyArchive = await _WriteSafetyArchiveAsync(password, cancellationToken);
        logger.LogWarning(
            "Restoring a backup taken {CreatedAt:u} by version {Version}. The current state has been archived to {SafetyArchive} " +
            "under the same password; restore that file to undo this.",
            manifest.CreatedAt, manifest.AppVersion, safetyArchive);

        try
        {
            var rows = await _RestoreDatabaseAsync(db, provider, zip, manifest, cancellationToken);
            var restoredFiles = _RestoreFiles(zip, manifest);

            return Result<BackupRestoreReport>.Success(new BackupRestoreReport(
                manifest.AppVersion,
                manifest.CreatedAt,
                manifest.Migrations.Target ?? string.Empty,
                manifest.Tables.Count,
                rows,
                restoredFiles,
                restoredFiles.Contains(BackupFormat.TokenProtectorKeyFile),
                safetyArchive));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Past this point the old database is gone, so there is nothing to roll back to except that file.
            // Say where it is in the message the admin actually sees, not only in the log.
            logger.LogCritical(ex, "The restore failed after the database was dropped. The previous state is in {SafetyArchive}.", safetyArchive);
            return Result<BackupRestoreReport>.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.BackupRestoreFailed,
                $"The restore failed after the database had already been emptied, and this server is now incomplete. " +
                $"The state from just before the restore was archived to '{safetyArchive}' under the password you " +
                $"just entered — restore that file to get back to where you were. Underlying error: {ex.Message}"));
        }
    }

    /// <summary>Copies the upload to a seekable file, still encrypted — the plaintext of the most sensitive file
    /// this application has never touches the disk. Nothing here inspects the bytes; that starts once the archive
    /// can be read backwards from its central directory.</summary>
    private static async Task _StageAsync(Stream upload, string staged, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        _RestrictToOwner(staged);
        await upload.CopyToAsync(file, cancellationToken);
    }

    private static BackupManifest _ReadManifest(ZipFile zip)
    {
        using var stream = BackupZip.OpenManifest(zip);
        return JsonSerializer.Deserialize<BackupManifest>(stream, BackupJson.Options)
            ?? throw new InvalidDataException("The archive's manifest is unreadable.");
    }

    private async Task<string> _WriteSafetyArchiveAsync(string password, CancellationToken cancellationToken)
    {
        await using var file = _CreateSafetyArchiveFile();
        _RestrictToOwner(file.Name);
        await exporter.WriteAsync(file, password, cancellationToken);
        return file.Name;
    }

    /// <summary>
    /// Opens the safety archive with <see cref="FileMode.CreateNew"/> and walks the name on if one is already
    /// there. A restore that fails and is retried within the same second would otherwise land on the same name,
    /// and the copy already sitting there can be the last surviving token-protector key.
    /// </summary>
    private FileStream _CreateSafetyArchiveFile()
    {
        var takenAt = DateTimeOffset.UtcNow;
        for (var attempt = 1; ; attempt++)
        {
            var path = Path.Combine(options.DataDirectory, BackupFormat.PreRestoreFileName(takenAt, attempt));
            try
            {
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (attempt < MaxSafetyArchiveAttempts && File.Exists(path))
            {
            }
        }
    }

    /// <summary>
    /// Drops what is there, rebuilds the schema at exactly the migration the archive was taken on, and fills it.
    /// Rebuilding to the archive's own state rather than the current one is what lets an older archive be restored
    /// at all: its rows fit that schema, and the migration run on the next start carries them forward.
    /// </summary>
    private static async Task<long> _RestoreDatabaseAsync(
        ServerDbContext db, Data.DatabaseProvider provider, ZipFile zip, BackupManifest manifest, CancellationToken cancellationToken)
    {
        var helper = db.GetService<ISqlGenerationHelper>();

        // Reverse insert order, so a table goes before anything it points at is gone.
        foreach (var table in BackupSchemaMap.Build(db.Model).Reverse())
            await db.Database.ExecuteSqlRawAsync(BackupSql.DropTable(helper, table.Name, table.Schema), cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            BackupSql.DropTable(helper, HistoryRepository.DefaultTableName, schema: null), cancellationToken);

        await db.GetService<IMigrator>().MigrateAsync(manifest.Migrations.Target, cancellationToken);

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var rows = 0L;
            foreach (var table in manifest.Tables)
            {
                await using var stream = BackupZip.OpenEntry(zip, table.Entry, $"table '{table.Name}'");
                rows += await BackupTableWriter.ReadAsync(connection, transaction, helper, provider, stream, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return rows;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Writes the identity files back, certificate first and token-protector key last. Only the files this build
    /// knows by name are written; anything else the archive happens to carry is left alone rather than dropped
    /// into the data directory on an uploaded file's say-so.
    /// </summary>
    private List<string> _RestoreFiles(ZipFile zip, BackupManifest manifest)
    {
        var restored = new List<string>();

        foreach (var name in ServerBackupOptions.ArchivedFiles)
        {
            var file = manifest.Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
            if (file is null)
            {
                logger.LogWarning("The archive carries no {File}; leaving the current one in place.", name);
                continue;
            }

            var target = Path.Combine(options.DataDirectory, name);
            var staged = target + ".restoring";
            using (var source = BackupZip.OpenEntry(zip, file.Entry, $"file '{file.Name}'"))
            using (var destination = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                _RestrictToOwner(staged);
                source.CopyTo(destination);
            }

            // Move rather than write in place: the window in which the file on disk is neither the old one nor the
            // whole new one is a rename, not a copy.
            File.Move(staged, target, overwrite: true);
            _RestrictToOwner(target);
            restored.Add(name);
        }

        foreach (var ignored in manifest.Files.Where(f => !ServerBackupOptions.ArchivedFiles.Contains(f.Name)))
            logger.LogWarning("Ignoring '{File}' in the archive: this server does not restore that file.", ignored.Name);

        return restored;
    }

    private static void _RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
            // Same best-effort stance as AesGcmTokenProtector takes on the key it writes.
        }
    }

    private void _TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not remove the uploaded archive at {Path}. Delete it by hand.", path);
        }
    }
}
