namespace EveUtils.Server.Backup;

/// <summary>
/// How large an uploaded archive may be. Kestrel caps a request body at 30 MB by default, which a real server's
/// database passes quickly; this raises it for the restore endpoint alone rather than globally. Generous but not
/// unlimited — an endpoint that accepts an unbounded body is a way to fill someone's disk, and an archive past this
/// size means something other than a backup is being uploaded.
/// </summary>
internal static class BackupUploadLimits
{
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;
}
