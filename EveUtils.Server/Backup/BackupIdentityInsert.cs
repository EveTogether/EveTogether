using System.Data.Common;
using EveUtils.Server.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace EveUtils.Server.Backup;

/// <summary>
/// The one place the providers genuinely differ. A restore must keep the original primary keys — a
/// <c>SyncedCharacter</c> is referenced by id from sessions and rosters — but each engine has its own opinion
/// about writing an explicit value into a column it generates itself:
///
/// <list type="bullet">
/// <item>SQLite and MySQL take the value and move their counter past it. Nothing to do.</item>
/// <item>SQL Server rejects it outright unless <c>IDENTITY_INSERT</c> is on for that table, one table at a time.</item>
/// <item>PostgreSQL takes the value but leaves its sequence where it was, so the first row inserted after the
/// restore collides with a key that already exists. The sequence has to be moved on afterwards.</item>
/// </list>
///
/// This is not a native dump per provider — the data and its ordering are the same file everywhere; this only
/// smooths over how each engine guards its generated keys.
/// </summary>
internal static class BackupIdentityInsert
{
    /// <summary>Run before inserting into <paramref name="table"/>.</summary>
    public static async Task BeginAsync(
        DbConnection connection, DbTransaction transaction, ISqlGenerationHelper helper,
        DatabaseProvider provider, BackupTableMap table, CancellationToken cancellationToken)
    {
        if (provider is not DatabaseProvider.SqlServer || table.StoreGeneratedKeyColumns.Count == 0)
            return;

        await _ExecuteAsync(connection, transaction,
            $"SET IDENTITY_INSERT {BackupSql.QualifiedName(helper, table)} ON", cancellationToken);
    }

    /// <summary>Run after inserting into <paramref name="table"/>, whether or not it had rows: an empty table on
    /// SQL Server still has to have IDENTITY_INSERT turned back off before the next one turns it on.</summary>
    public static async Task EndAsync(
        DbConnection connection, DbTransaction transaction, ISqlGenerationHelper helper,
        DatabaseProvider provider, BackupTableMap table, CancellationToken cancellationToken)
    {
        if (table.StoreGeneratedKeyColumns.Count == 0)
            return;

        switch (provider)
        {
            case DatabaseProvider.SqlServer:
                await _ExecuteAsync(connection, transaction,
                    $"SET IDENTITY_INSERT {BackupSql.QualifiedName(helper, table)} OFF", cancellationToken);
                break;

            case DatabaseProvider.PostgreSql:
                foreach (var column in table.StoreGeneratedKeyColumns)
                    await _ExecuteAsync(connection, transaction, _PostgreSqlSetval(helper, table, column), cancellationToken);
                break;

            case DatabaseProvider.Sqlite:
            case DatabaseProvider.MySql:
                break;

            default:
                throw new InvalidOperationException($"Provider '{provider}' has no restore behaviour defined.");
        }
    }

    /// <summary>
    /// Moves the identity sequence past the largest key just inserted. The third argument to <c>setval</c> is
    /// <c>is_called</c>: false for an empty table means the sequence still hands out 1, rather than skipping it.
    /// </summary>
    private static string _PostgreSqlSetval(ISqlGenerationHelper helper, BackupTableMap table, string column)
    {
        var qualified = BackupSql.QualifiedName(helper, table);
        var quotedColumn = helper.DelimitIdentifier(column);
        var tableLiteral = _SqlLiteral(qualified);
        var columnLiteral = _SqlLiteral(column);

        return $"""
            SELECT setval(
                pg_get_serial_sequence({tableLiteral}, {columnLiteral}),
                COALESCE((SELECT MAX({quotedColumn}) FROM {qualified}), 1),
                (SELECT MAX({quotedColumn}) FROM {qualified}) IS NOT NULL)
            WHERE pg_get_serial_sequence({tableLiteral}, {columnLiteral}) IS NOT NULL
            """;
    }

    /// <summary>Single-quoted SQL string literal. Only ever fed identifiers that came out of the EF model, but
    /// escaped all the same — a table name reaching a query as text is exactly the shape that goes wrong later.</summary>
    private static string _SqlLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static async Task _ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
