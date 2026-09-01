namespace EveUtils.Server.Backup;

/// <summary>A written archive: what it says about itself, and how large the finished file turned out.</summary>
internal sealed record BackupArchiveResult(BackupManifest Manifest, long SizeBytes);
