using System.IO.Compression;
using System.Text.Json;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Storage;
using Microsoft.Data.Sqlite;

namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>
/// Builds a fresh read-only SDE SQLite file from a downloaded JSONL zip. Streams each dataset line-by-line
/// (no full-DOM), bulk-inserts in one transaction with build-time pragmas (journal/sync off — it is a throwaway
/// file until the swap), then creates the indexes. The slot/hardpoint table is pre-computed while reading
/// typeDogma so fit parsers never join dogma at runtime. Heavy datasets (map*, materials, blueprints) are
/// ignored — only the minimal subset is imported (data-minimalisation).
/// </summary>
public sealed class SdeSqliteBuilder
{
    private const int ReportEvery = 2000;

    // Entry names are flat in the zip (verified build 3374020 — no sde/ submap). Order: dependency-light first.
    private static readonly string[] Datasets =
        ["categories.jsonl", "groups.jsonl", "dogmaAttributes.jsonl", "dogmaEffects.jsonl", "types.jsonl", "typeDogma.jsonl"];

    /// <summary>
    /// Builds the store at <paramref name="outputPath"/> from <paramref name="zipPath"/>. Reports the Processing
    /// phase via <paramref name="progress"/> with a pre-counted total. Overwrites any existing output file.
    /// </summary>
    public void Build(
        string zipPath,
        string outputPath,
        SdeVersion version,
        IProgress<SdeImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        progress?.Report(new SdeImportProgress(SdeImportPhase.Preparing));
        var total = CountTotalLines(archive, cancellationToken);
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = outputPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY;");
        foreach (var ddl in SdeSchema.CreateTables)
            Execute(connection, ddl);

        long processed = 0;
        using (var transaction = connection.BeginTransaction())
        {
            var writer = new TableWriters(connection, transaction);
            foreach (var dataset in Datasets)
            {
                var entry = archive.GetEntry(dataset);
                if (entry is null)
                    continue;
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (line.Length == 0)
                        continue;
                    using (var document = JsonDocument.Parse(line))
                        writer.Insert(dataset, document.RootElement);
                    processed++;
                    if (processed % ReportEvery == 0)
                        progress?.Report(new SdeImportProgress(
                            SdeImportPhase.Processing, ProcessedItems: processed, TotalItems: total, CurrentDataset: dataset));
                }
            }

            WriteMeta(connection, transaction, version);
            transaction.Commit();
        }

        progress?.Report(new SdeImportProgress(SdeImportPhase.Finalizing, ProcessedItems: total, TotalItems: total));
        foreach (var indexDdl in SdeSchema.CreateIndexes)
            Execute(connection, indexDdl);
    }

    private static long CountTotalLines(ZipArchive archive, CancellationToken cancellationToken)
    {
        long total = 0;
        var buffer = new byte[1 << 20];
        foreach (var dataset in Datasets)
        {
            var entry = archive.GetEntry(dataset);
            if (entry is null)
                continue;
            using var stream = entry.Open();
            int read;
            var lastByte = (byte)'\n';
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var i = 0; i < read; i++)
                    if (buffer[i] == (byte)'\n')
                        total++;
                lastByte = buffer[read - 1];
            }
            // A final line without a trailing newline still produces a record.
            if (lastByte != (byte)'\n')
                total++;
        }
        return total;
    }

    private static void WriteMeta(SqliteConnection connection, SqliteTransaction transaction, SdeVersion version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Meta (key, value) VALUES ($k, $v);";
        var key = command.Parameters.Add("$k", SqliteType.Text);
        var value = command.Parameters.Add("$v", SqliteType.Text);
        key.Value = SdeSchema.MetaBuildNumber;
        value.Value = version.BuildNumber.ToString();
        command.ExecuteNonQuery();
        key.Value = SdeSchema.MetaReleaseDate;
        value.Value = version.ReleaseDate.ToString("O");
        command.ExecuteNonQuery();
        key.Value = SdeSchema.MetaSchemaVersion;
        value.Value = SdeSchema.SchemaVersion.ToString();
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
