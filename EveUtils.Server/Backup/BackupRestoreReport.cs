namespace EveUtils.Server.Backup;

/// <summary>What a completed restore put back, for the panel to show before the server shuts itself down.</summary>
/// <param name="ArchiveAppVersion">Version of the server that wrote the archive.</param>
/// <param name="ArchiveCreatedAt">When the archive was taken.</param>
/// <param name="MigrationTarget">Migration the schema was rebuilt to; the next start migrates it forward from here.</param>
/// <param name="Tables">Tables filled.</param>
/// <param name="Rows">Rows inserted across all of them.</param>
/// <param name="FilesRestored">Data-directory files written back.</param>
/// <param name="TokenProtectorKeyRestored">False means every paired character's refresh token is now unreadable and
/// the new-identity guard will refuse the next start (ET-94) — worth saying out loud rather than reporting success.</param>
/// <param name="SafetyArchivePath">The archive taken of the previous state just before it was dropped.</param>
internal sealed record BackupRestoreReport(
    string ArchiveAppVersion,
    DateTimeOffset ArchiveCreatedAt,
    string MigrationTarget,
    int Tables,
    long Rows,
    IReadOnlyList<string> FilesRestored,
    bool TokenProtectorKeyRestored,
    string SafetyArchivePath);
