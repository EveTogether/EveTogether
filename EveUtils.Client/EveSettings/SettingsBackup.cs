using System.Text.Json.Serialization;

namespace EveUtils.Client.EveSettings;

/// <summary>Why a backup exists — shown in the list so an automatic one is telling from one the user asked for.</summary>
public enum BackupReason
{
    /// <summary>The user pressed "back up this profile".</summary>
    Manual,

    /// <summary>Taken automatically right before a sync overwrote files.</summary>
    BeforeSync,

    /// <summary>Taken automatically right before an older backup was restored over the profile.</summary>
    BeforeRestore
}

/// <summary>One file inside a backup, described by name rather than by id so the list stays readable.</summary>
public sealed record SettingsBackupEntry(
    string FileName,
    SettingsFileKind Kind,
    long Id,
    string Name,
    DateTimeOffset LastModifiedUtc,
    long SizeBytes);

/// <summary>
/// What a backup folder holds, written beside the files as <c>backup.json</c>. Deliberately self-describing: the
/// profile it came from, when, why, and every character and account in it <em>with the name they had</em>. That is
/// exactly what a portable preset needs to be readable on another machine (ET-61), so a preset can be this format
/// plus a wrapper instead of a second format invented later.
/// </summary>
public sealed record SettingsBackupManifest
{
    /// <summary>Bumped when the shape changes; a folder with an unknown version is listed but not restored.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required BackupReason Reason { get; init; }

    /// <summary>One line on what triggered this backup, e.g. the sync it was taken ahead of.</summary>
    public string Note { get; init; } = string.Empty;

    public required string ProfileName { get; init; }
    public required string ProfileDirectory { get; init; }
    public required string InstallRoot { get; init; }

    /// <summary>The EVE Together build that wrote it — a preset read elsewhere should know where it came from.</summary>
    public required string AppVersion { get; init; }

    public required IReadOnlyList<SettingsBackupEntry> Entries { get; init; }

    [JsonIgnore]
    public int CharacterCount => Entries.Count(entry => entry.Kind == SettingsFileKind.Character);

    [JsonIgnore]
    public int AccountCount => Entries.Count(entry => entry.Kind == SettingsFileKind.Account);
}

/// <summary>A backup on disk: its manifest plus where it lives, so it can be shown, restored or deleted.</summary>
public sealed record SettingsBackup(string Id, string DirectoryPath, SettingsBackupManifest Manifest)
{
    /// <summary>Where the copied <c>.dat</c> files sit, kept apart from the manifest.</summary>
    public string FilesDirectory => Path.Combine(DirectoryPath, FilesFolderName);

    public const string FilesFolderName = "files";
    public const string ManifestFileName = "backup.json";

    public long TotalSizeBytes => Manifest.Entries.Sum(entry => entry.SizeBytes);

    /// <summary>False for a folder written by a newer build — it is still listed, but restoring it is refused
    /// rather than half-understood.</summary>
    public bool CanRestore => Manifest.FormatVersion <= SettingsBackupManifest.CurrentFormatVersion;
}
