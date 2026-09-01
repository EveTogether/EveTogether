using System.Data.Common;
using System.Text;
using System.Text.Json;
using EveUtils.Server.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace EveUtils.Server.Backup;

// EveUtils.Server.Stream (the DPS hub) shadows System.IO.Stream inside this project.
using Stream = System.IO.Stream;

/// <summary>
/// Fills one table back up from its archive entry. The entry's own header line decides the columns and the key
/// handling — not the live model — so an archive taken before a migration inserts against the schema it was taken
/// from, which is the schema the restore has just rebuilt.
/// </summary>
internal static class BackupTableWriter
{
    public static async Task<long> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        ISqlGenerationHelper helper,
        DatabaseProvider provider,
        Stream source,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(source, Encoding.UTF8);

        var headerLine = await reader.ReadLineAsync(cancellationToken)
            ?? throw new InvalidDataException("A table entry in the backup archive is empty.");
        var table = JsonSerializer.Deserialize<BackupTableMap>(headerLine, BackupJson.Options)
            ?? throw new InvalidDataException("A table entry in the backup archive has an unreadable header.");

        await BackupIdentityInsert.BeginAsync(connection, transaction, helper, provider, table, cancellationToken);

        var rows = 0L;
        var rowsPerBatch = BackupSql.RowsPerBatch(table);
        var batch = new List<object?[]>(rowsPerBatch);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
                continue;

            batch.Add(_ParseRow(line, table));
            if (batch.Count < rowsPerBatch)
                continue;

            rows += await _InsertBatchAsync(connection, transaction, helper, table, batch, cancellationToken);
            batch.Clear();
        }

        if (batch.Count > 0)
            rows += await _InsertBatchAsync(connection, transaction, helper, table, batch, cancellationToken);

        await BackupIdentityInsert.EndAsync(connection, transaction, helper, provider, table, cancellationToken);
        return rows;
    }

    private static object?[] _ParseRow(string line, BackupTableMap table)
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.ValueKind is not JsonValueKind.Array)
            throw new InvalidDataException($"A row in table '{table.Name}' is not a JSON array.");

        var values = document.RootElement.EnumerateArray().ToArray();
        if (values.Length != table.Columns.Count)
        {
            throw new InvalidDataException(
                $"A row in table '{table.Name}' has {values.Length} values but the entry declares {table.Columns.Count} columns.");
        }

        var row = new object?[table.Columns.Count];
        for (var i = 0; i < row.Length; i++)
        {
            var column = table.Columns[i];
            row[i] = BackupValueCodec.Read(values[i], column.Type, $"{table.Name}.{column.Name}");
        }

        return row;
    }

    private static async Task<int> _InsertBatchAsync(
        DbConnection connection, DbTransaction transaction, ISqlGenerationHelper helper,
        BackupTableMap table, List<object?[]> batch, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BackupSql.InsertBatch(helper, table, batch.Count);

        for (var row = 0; row < batch.Count; row++)
        {
            for (var column = 0; column < table.Columns.Count; column++)
            {
                command.Parameters.Add(BackupSql.CreateParameter(
                    command, helper, BackupSql.ParameterName(row, column), table.Columns[column].Type, batch[row][column]));
            }
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
        return batch.Count;
    }
}
