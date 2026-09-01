using System.Text.Json;
using EveUtils.Shared.App;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EveUtils.Server.Backup;

// EveUtils.Server.Stream (the DPS hub) shadows System.IO.Stream inside this project.
using Stream = System.IO.Stream;

/// <summary>
/// Writes a complete, encrypted server archive straight onto the caller's stream. Nothing is staged on disk on
/// the way out: the rows stream from the database through the encrypting ZIP into the response, so an
/// interrupted download leaves no readable remains anywhere (ET-99) — and it never has to hold a database in
/// memory to find out how big it is.
/// </summary>
internal sealed class BackupExporter(
    IDbContextFactory<ServerDbContext> contextFactory,
    ServerInfo serverInfo,
    ServerBackupOptions options) : IScopedService
{
    public async Task<BackupManifest> WriteAsync(Stream destination, string password, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var helper = db.GetService<ISqlGenerationHelper>();
        var tables = BackupSchemaMap.Build(db.Model);

        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var manifest = new BackupManifest
        {
            CreatedAt = DateTimeOffset.UtcNow,
            AppVersion = AppInfo.Version,
            ServerName = serverInfo.Name,
            Provider = BackupProviderName.Resolve(db.Database.ProviderName),
            Migrations = new BackupMigrationState { Applied = applied, Target = applied.LastOrDefault() },
        };

        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // Disposing the writer is what appends the central directory, and a ZIP without one is not a ZIP to
            // anything — which is exactly how a download that broke off announces itself.
            await using var zip = BackupZip.CreateWriter(destination, password);

            for (var order = 0; order < tables.Count; order++)
                manifest.Tables.Add(await _WriteTableAsync(zip, connection, helper, tables[order], order, manifest.CreatedAt, cancellationToken));

            foreach (var name in ServerBackupOptions.ArchivedFiles)
            {
                if (_WriteFile(zip, name, manifest.CreatedAt) is { } file)
                    manifest.Files.Add(file);
            }

            // Last, because its checksums are only known once everything else has been written. A reader opens the
            // ZIP by its central directory, so position in the file does not matter.
            _WriteManifest(zip, manifest);

            return manifest;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<BackupTableManifest> _WriteTableAsync(
        ZipOutputStream zip, System.Data.Common.DbConnection connection, ISqlGenerationHelper helper,
        BackupTableMap table, int order, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        var entryName = BackupFormat.DatabaseEntry(order, table.Name);
        zip.PutNextEntry(BackupZip.Entry(entryName, createdAt));
        using var hashing = new BackupHashingStream(zip);

        var rows = await BackupTableReader.WriteAsync(connection, helper, table, hashing, cancellationToken);
        var written = new BackupTableManifest { Name = table.Name, Entry = entryName, Rows = rows, Sha256 = hashing.Digest() };

        zip.CloseEntry();
        return written;
    }

    /// <summary>Copies one data-directory file in. Returns null when it is not there — the certificate and the key
    /// are both created on first start, so a server that has never run has neither.</summary>
    private BackupFileManifest? _WriteFile(ZipOutputStream zip, string name, DateTimeOffset createdAt)
    {
        var path = Path.Combine(options.DataDirectory, name);
        if (!File.Exists(path))
            return null;

        var entryName = BackupFormat.DataEntryPrefix + name;
        zip.PutNextEntry(BackupZip.Entry(entryName, createdAt));

        using var source = File.OpenRead(path);
        using var hashing = new BackupHashingStream(zip);
        source.CopyTo(hashing);

        var written = new BackupFileManifest
        {
            Name = name,
            Entry = entryName,
            SizeBytes = hashing.BytesWritten,
            Sha256 = hashing.Digest(),
        };

        zip.CloseEntry();
        return written;
    }

    private static void _WriteManifest(ZipOutputStream zip, BackupManifest manifest)
    {
        zip.PutNextEntry(BackupZip.Entry(BackupFormat.ManifestEntry, manifest.CreatedAt));
        zip.Write(JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJson.Options));
        zip.CloseEntry();
    }
}
