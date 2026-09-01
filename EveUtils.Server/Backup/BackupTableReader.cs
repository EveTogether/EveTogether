using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;

namespace EveUtils.Server.Backup;

// EveUtils.Server.Stream (the DPS hub) shadows System.IO.Stream inside this project.
using Stream = System.IO.Stream;

/// <summary>
/// Streams one table out of the database into its archive entry as JSON Lines: the table's own description on the
/// first line, then one JSON array per row. Rows as arrays rather than objects keeps the column names out of every
/// row, and the header line makes the file readable on its own — a restore never has to consult the model that
/// produced it.
/// </summary>
internal static class BackupTableReader
{
    public static async Task<long> WriteAsync(
        DbConnection connection,
        ISqlGenerationHelper helper,
        BackupTableMap table,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await destination.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(table, BackupJson.Options), cancellationToken);
        destination.WriteByte((byte)'\n');

        await using var command = connection.CreateCommand();
        command.CommandText = BackupSql.SelectAll(helper, table);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await using var writer = new Utf8JsonWriter(destination);

        var rows = 0L;
        while (await reader.ReadAsync(cancellationToken))
        {
            writer.WriteStartArray();
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var column = table.Columns[i];
                BackupValueCodec.Write(writer, reader.IsDBNull(i) ? null : reader.GetValue(i), column.Type,
                    $"{table.Name}.{column.Name}");
            }

            writer.WriteEndArray();
            await writer.FlushAsync(cancellationToken);
            destination.WriteByte((byte)'\n');
            writer.Reset();
            rows++;
        }

        return rows;
    }
}
