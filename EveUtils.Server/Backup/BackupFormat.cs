namespace EveUtils.Server.Backup;

/// <summary>
/// The on-disk shape of a server backup archive (<c>.etbackup</c>), in one place because every byte of it is a
/// compatibility promise: once an admin has downloaded an archive, every later build has to be able to read it.
///
/// A file is an encrypted envelope (see <see cref="BackupEnvelope"/>) whose plaintext is a ZIP:
/// <code>
/// manifest.json                       written last, read first (random access)
/// database/000.&lt;Table&gt;.jsonl       one file per table; the number is the insert order
/// data/token-protector.key            decrypts SyncedCharacter.RefreshTokenCipher — the reason this exists
/// data/server-cert.pfx                the pinned TLS identity; without it every client must pair again
/// </code>
/// Deliberately absent: <c>appsettings.*</c>, connection strings and the ESI secret (they are per-installation),
/// and <c>esi-cache/</c> + <c>sde/</c> (both rebuild themselves, and the SDE is large).
/// </summary>
internal static class BackupFormat
{
    /// <summary>Content layout version, in the manifest. Bumped when the ZIP layout or the row encoding changes;
    /// separate from the envelope's own version, which covers only the crypto framing.</summary>
    public const int ContentVersion = 1;

    public const string FileExtension = ".etbackup";

    public const string ManifestEntry = "manifest.json";
    public const string DatabaseEntryPrefix = "database/";
    public const string DataEntryPrefix = "data/";

    public const string TokenProtectorKeyFile = "token-protector.key";
    public const string ServerCertificateFile = "server-cert.pfx";

    /// <summary>Entry name for the table inserted at <paramref name="order"/>. The number is part of the name so the
    /// insert order survives in the archive itself rather than having to be recomputed against a model that may
    /// have moved on since.</summary>
    public static string DatabaseEntry(int order, string tableName) =>
        $"{DatabaseEntryPrefix}{order:D3}.{tableName}.jsonl";

    /// <summary>Download file name: server-scoped and timestamped so several archives can sit in one folder.</summary>
    public static string DownloadFileName(DateTimeOffset takenAt) =>
        $"eve-together-backup-{takenAt.UtcDateTime:yyyyMMdd-HHmmss}Z{FileExtension}";

    /// <summary>
    /// Name of the safety copy taken just before a restore drops the current database. <paramref name="attempt"/>
    /// distinguishes copies taken in the same second — a restore that failed and was retried straight away — because
    /// the earlier one may hold the only surviving token-protector key and is never overwritten.
    /// </summary>
    public static string PreRestoreFileName(DateTimeOffset takenAt, int attempt = 1)
    {
        var suffix = attempt > 1 ? $"-{attempt}" : string.Empty;
        return $"pre-restore-{takenAt.UtcDateTime:yyyyMMdd-HHmmss}Z{suffix}{FileExtension}";
    }
}
