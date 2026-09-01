namespace EveUtils.Server.Backup;

/// <summary>One table as the backup engine sees it: a name, its columns in a fixed order, and what it needs to be
/// filled back up.</summary>
/// <param name="Name">Table name as the provider knows it.</param>
/// <param name="Schema">Schema, when the provider uses one.</param>
/// <param name="Columns">Every column in the table, ordered by name so an archive is byte-identical across runs.</param>
/// <param name="KeyColumns">Primary-key columns — the export orders rows by these so a checksum is reproducible.</param>
/// <param name="StoreGeneratedKeyColumns">Key columns the database fills in itself. Restoring keeps the original
/// values, which is what the providers need help with: SQL Server refuses them without <c>IDENTITY_INSERT</c>, and
/// PostgreSQL accepts them but leaves its sequence behind.</param>
internal sealed record BackupTableMap(
    string Name,
    string? Schema,
    IReadOnlyList<BackupTableColumn> Columns,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> StoreGeneratedKeyColumns);

internal sealed record BackupTableColumn(string Name, BackupColumnType Type);
