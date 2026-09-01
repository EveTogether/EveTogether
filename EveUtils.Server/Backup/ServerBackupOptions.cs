namespace EveUtils.Server.Backup;

/// <summary>Where the backup engine reads and writes the server's identity files. Registered by the host, which is
/// the only place that knows the resolved data directory (ET-94).</summary>
internal sealed class ServerBackupOptions(string dataDirectory)
{
    public string DataDirectory { get; } = dataDirectory;

    /// <summary>
    /// The data-directory files an archive carries, in the order a restore writes them back — the token-protector
    /// key last, for the same reason the ET-94 data move puts it last. A restore interrupted after the key but
    /// before the database would leave a key next to data it cannot decrypt, and nothing would say so; interrupted
    /// the other way round it runs into the new-identity guard on the next start, which is loud and recoverable.
    /// </summary>
    public static readonly IReadOnlyList<string> ArchivedFiles =
    [
        BackupFormat.ServerCertificateFile,
        BackupFormat.TokenProtectorKeyFile,
    ];
}
