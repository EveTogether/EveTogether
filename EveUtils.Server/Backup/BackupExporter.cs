using System.IO.Compression;
using System.Text.Json;
using EveUtils.Shared.App;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EveUtils.Server.Backup;

// EveUtils.Server.Stream (the DPS hub) shadows System.IO.Stream inside this project.
using Stream = System.IO.Stream;

/// <summary>
/// Writes a complete, encrypted server archive straight onto <paramref name="destination"/>. Nothing is staged on
/// disk on the way out: the rows stream from the database through the ZIP and the cipher into the response, so an
/// interrupted download leaves no readable remains anywhere (ET-99).
/// </summary>
internal sealed class BackupExporter(
    IDbContextFactory<ServerDbContext> contextFactory,
    ServerInfo serverInfo,
    ServerBackupOptions options) : IScopedService
{
    public async Task<BackupArchiveResult> WriteAsync(Stream destination, string password, CancellationToken cancellationToken)
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
            var envelope = BackupEnvelope.CreateWriter(destination, password);
            // Disposed in this order on purpose: the ZIP's central directory has to be written while the cipher
            // stream can still take it, and only the envelope's own dispose seals the final chunk.
            await using (envelope)
            {
                using var zip = new ZipArchive(envelope, ZipArchiveMode.Create, leaveOpen: true);

                for (var order = 0; order < tables.Count; order++)
                    manifest.Tables.Add(await _WriteTableAsync(zip, connection, helper, tables[order], order, cancellationToken));

                foreach (var name in ServerBackupOptions.ArchivedFiles)
                {
                    if (_WriteFile(zip, name) is { } file)
                        manifest.Files.Add(file);
                }

                // Last, because its checksums are only known once everything else has been written. A reader opens
                // the ZIP by its central directory, so position in the file does not matter.
                _WriteManifest(zip, manifest);
            }

            return new BackupArchiveResult(manifest, envelope.BytesWritten);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<BackupTableManifest> _WriteTableAsync(
        ZipArchive zip, System.Data.Common.DbConnection connection, ISqlGenerationHelper helper,
        BackupTableMap table, int order, CancellationToken cancellationToken)
    {
        var entryName = BackupFormat.DatabaseEntry(order, table.Name);
        await using var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        await using var hashing = new BackupHashingStream(entry);

        var rows = await BackupTableReader.WriteAsync(connection, helper, table, hashing, cancellationToken);
        return new BackupTableManifest { Name = table.Name, Entry = entryName, Rows = rows, Sha256 = hashing.Digest() };
    }

    /// <summary>Copies one data-directory file in. Returns null when it is not there — the certificate and the key
    /// are both created on first start, so a server that has never run has neither.</summary>
    private BackupFileManifest? _WriteFile(ZipArchive zip, string name)
    {
        var path = Path.Combine(options.DataDirectory, name);
        if (!File.Exists(path))
            return null;

        var entryName = BackupFormat.DataEntryPrefix + name;
        using var source = File.OpenRead(path);
        using var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        using var hashing = new BackupHashingStream(entry);
        source.CopyTo(hashing);

        return new BackupFileManifest
        {
            Name = name,
            Entry = entryName,
            SizeBytes = hashing.BytesWritten,
            Sha256 = hashing.Digest(),
        };
    }

    private static void _WriteManifest(ZipArchive zip, BackupManifest manifest)
    {
        using var entry = zip.CreateEntry(BackupFormat.ManifestEntry, CompressionLevel.Optimal).Open();
        entry.Write(JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJson.Options));
    }
}
