using EveUtils.Server.Data;

namespace EveUtils.Server.Backup;

/// <summary>
/// What the archive says about itself. Written last into the ZIP and read first on restore: the compatibility
/// decision and every checksum comparison happen before a single table is touched.
/// </summary>
internal sealed class BackupManifest
{
    public int FormatVersion { get; set; } = BackupFormat.ContentVersion;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The build that wrote it. Shown to the admin; the restore decides on
    /// <see cref="Migrations"/>, not on this string.</summary>
    public string AppVersion { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    /// <summary>The engine that wrote the rows. Column types in the archive are that engine's store types, so a
    /// restore onto a different one is refused rather than silently reinterpreted.</summary>
    public DatabaseProvider Provider { get; set; }

    public BackupMigrationState Migrations { get; set; } = new();

    /// <summary>In insert order — the order the restore walks them in.</summary>
    public List<BackupTableManifest> Tables { get; set; } = [];

    public List<BackupFileManifest> Files { get; set; } = [];
}

/// <summary>
/// Where the source database stood in the migration stack. This is how <c>__EFMigrationsHistory</c> travels: not
/// as a table of rows to insert, but as the list EF itself will rewrite when the restore migrates a rebuilt
/// database up to exactly this point. It is also the compatibility test — a migration named here that this build
/// does not have means the archive came from a newer server.
/// </summary>
internal sealed class BackupMigrationState
{
    public List<string> Applied { get; set; } = [];

    /// <summary>The last applied migration: the state the restore rebuilds the schema to.</summary>
    public string? Target { get; set; }
}

internal sealed class BackupTableManifest
{
    public string Name { get; set; } = string.Empty;
    public string Entry { get; set; } = string.Empty;
    public long Rows { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class BackupFileManifest
{
    /// <summary>File name as it lands back in the server data directory.</summary>
    public string Name { get; set; } = string.Empty;
    public string Entry { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}
