using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage;

namespace EveUtils.Server.Backup;

/// <summary>
/// The handful of statements the backup engine issues, built through the provider's own
/// <see cref="ISqlGenerationHelper"/> so quoting and parameter prefixes are right on all four providers without a
/// dialect of our own. This is what "provider-neutral export" means in practice: EF supplies the grammar, the
/// engine only decides which tables and columns.
/// </summary>
internal static class BackupSql
{
    /// <summary>Kept well under the tightest of the four limits (SQL Server allows 2100 parameters per statement)
    /// so one batch shape works everywhere.</summary>
    public const int MaxParametersPerStatement = 1000;

    public static string QualifiedName(ISqlGenerationHelper helper, BackupTableMap table) =>
        helper.DelimitIdentifier(table.Name, table.Schema);

    public static string SelectAll(ISqlGenerationHelper helper, BackupTableMap table)
    {
        var columns = string.Join(", ", table.Columns.Select(c => helper.DelimitIdentifier(c.Name)));
        var sql = $"SELECT {columns} FROM {QualifiedName(helper, table)}";

        // Ordered by key so two exports of an unchanged database are byte-identical, which is what makes the
        // per-file checksums in the manifest worth comparing.
        return table.KeyColumns.Count == 0
            ? sql
            : $"{sql} ORDER BY {string.Join(", ", table.KeyColumns.Select(helper.DelimitIdentifier))}";
    }

    public static string DropTable(ISqlGenerationHelper helper, string name, string? schema) =>
        $"DROP TABLE IF EXISTS {helper.DelimitIdentifier(name, schema)}";

    /// <summary>How many rows fit in one multi-row INSERT for this table.</summary>
    public static int RowsPerBatch(BackupTableMap table) =>
        Math.Max(1, MaxParametersPerStatement / Math.Max(1, table.Columns.Count));

    /// <summary>
    /// A multi-row INSERT with one parameter per value. Parameters rather than inlined literals throughout: the
    /// values here come from an uploaded file, and a backup restore is the last place to be building SQL by
    /// concatenation.
    /// </summary>
    public static string InsertBatch(ISqlGenerationHelper helper, BackupTableMap table, int rowCount)
    {
        var columns = string.Join(", ", table.Columns.Select(c => helper.DelimitIdentifier(c.Name)));
        var sql = new StringBuilder($"INSERT INTO {QualifiedName(helper, table)} ({columns}) VALUES ");

        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                sql.Append(", ");

            sql.Append('(');
            for (var column = 0; column < table.Columns.Count; column++)
            {
                if (column > 0)
                    sql.Append(", ");
                sql.Append(helper.GenerateParameterName(ParameterName(row, column)));
            }

            sql.Append(')');
        }

        return sql.ToString();
    }

    public static string ParameterName(int row, int column) =>
        string.Create(CultureInfo.InvariantCulture, $"p{row}_{column}");

    public static DbParameter CreateParameter(DbCommand command, ISqlGenerationHelper helper, string name, BackupColumnType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = helper.GenerateParameterName(name);
        parameter.DbType = DbTypeFor(type);
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }

    /// <summary>Set explicitly rather than inferred from the value: a null carries no type, and a provider that has
    /// to guess at one binds it as text.</summary>
    public static DbType DbTypeFor(BackupColumnType type) => type switch
    {
        BackupColumnType.Boolean => DbType.Boolean,
        BackupColumnType.Byte => DbType.Byte,
        BackupColumnType.Int16 => DbType.Int16,
        BackupColumnType.Int32 => DbType.Int32,
        BackupColumnType.Int64 => DbType.Int64,
        BackupColumnType.Decimal => DbType.Decimal,
        BackupColumnType.Double => DbType.Double,
        BackupColumnType.Single => DbType.Single,
        BackupColumnType.String => DbType.String,
        BackupColumnType.Guid => DbType.Guid,
        BackupColumnType.DateTime => DbType.DateTime2,
        BackupColumnType.DateTimeOffset => DbType.DateTimeOffset,
        BackupColumnType.TimeSpan => DbType.Time,
        BackupColumnType.Bytes => DbType.Binary,
        _ => throw new InvalidOperationException($"Backup column type {type} has no DbType."),
    };
}
