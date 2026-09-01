namespace EveUtils.Shared.Modules.Backup.Entities;

/// <summary>
/// One record of a server backup archive leaving the machine. The archive decrypts every stored refresh token, so
/// who took a copy and when is itself part of the security story (ET-99) — the panel shows this list. Server-only;
/// the table lands in the server DB.
///
/// Written when the download starts, not when it finishes. The archive is encrypted block by block, so a transfer
/// that broke off halfway still handed over every block that did arrive; an attempt that never completed is worth
/// as much to whoever reads this list as one that did. Who and when is all it keeps.
/// </summary>
public sealed class BackupDownload
{
    public int Id { get; set; }

    /// <summary>The admin who downloaded it. A plain scalar, not an FK: deleting the admin user must not
    /// take the record of what they carried off the server with it.</summary>
    public int AdminUserId { get; set; }

    /// <summary>Copied at download time so the record still reads correctly after a rename or a deletion.</summary>
    public string AdminUsername { get; set; } = string.Empty;

    public DateTimeOffset DownloadedAt { get; set; }

    /// <summary>Build that produced the archive — the archive is only restorable on this version or newer.</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>Name the browser was offered, so a file on disk can be matched back to this row.</summary>
    public string FileName { get; set; } = string.Empty;
}
