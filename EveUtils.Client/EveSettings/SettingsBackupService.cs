using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveUtils.Shared.App;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.EveSettings;

/// <summary>
/// Keeps snapshots of whole settings profiles and puts them back.
///
/// They live in EVE Together's own data directory, not in a timestamped folder inside the profile: the EVE launcher
/// reads that folder too, and a profile the user deletes or lets the launcher reset would take its own safety net
/// with it. Here they survive both, and they are all in one place to review.
///
/// A backup always covers the <em>whole</em> profile, not just the files a sync is about to touch — restoring then
/// means "put this profile back the way it was", which is a promise that can be kept.
/// </summary>
public sealed class SettingsBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root;

    /// <param name="dataDirectory">EVE Together's per-instance data directory (<c>ClientServices.DataDirectory()</c>).</param>
    public SettingsBackupService(string dataDirectory)
    {
        _root = Path.Combine(dataDirectory, "eve-settings-backups");
    }

    /// <summary>Where the backups are kept — shown in the UI so the user can find them without us.</summary>
    public string RootDirectory => _root;

    /// <summary>
    /// Snapshots every character and account file of <paramref name="profile"/>. <paramref name="names"/> supplies
    /// the display name per file id so the manifest reads in names rather than ids.
    /// </summary>
    public Result<SettingsBackup> Create(
        EveSettingsProfile profile,
        string installRoot,
        IReadOnlyDictionary<long, string> names,
        BackupReason reason,
        string note)
    {
        var files = profile.Characters.Concat(profile.Accounts).ToList();
        if (files.Count == 0)
            return Result<SettingsBackup>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                $"Profile {profile.Name} holds no character or account settings to back up."));

        var createdAt = DateTimeOffset.UtcNow;
        var id = _NewBackupId(createdAt, profile.Name);
        var directory = Path.Combine(_root, id);
        var filesDirectory = Path.Combine(directory, SettingsBackup.FilesFolderName);

        var entries = new List<SettingsBackupEntry>(files.Count);
        try
        {
            Directory.CreateDirectory(filesDirectory);
            foreach (var file in files)
            {
                File.Copy(file.FullPath, Path.Combine(filesDirectory, file.FileName), overwrite: true);
                entries.Add(new SettingsBackupEntry(
                    file.FileName, file.Kind, file.Id,
                    names.TryGetValue(file.Id, out var name) ? name : string.Empty,
                    file.LastModifiedUtc, file.SizeBytes));
            }

            var manifest = new SettingsBackupManifest
            {
                CreatedAtUtc = createdAt,
                Reason = reason,
                Note = note,
                ProfileName = profile.Name,
                ProfileDirectory = profile.DirectoryPath,
                InstallRoot = installRoot,
                AppVersion = AppInfo.Version,
                Entries = entries
            };
            File.WriteAllText(Path.Combine(directory, SettingsBackup.ManifestFileName),
                JsonSerializer.Serialize(manifest, JsonOptions));

            return Result<SettingsBackup>.Success(new SettingsBackup(id, directory, manifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _TryDeleteDirectory(directory);   // a half-written backup is worse than none: it would restore holes
            return Result<SettingsBackup>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.FileIoFailed,
                $"Could not write the backup: {ex.Message}"));
        }
    }

    /// <summary>Every readable backup, newest first. A folder whose manifest is missing or unreadable is skipped
    /// rather than shown as an empty row.</summary>
    public IReadOnlyList<SettingsBackup> List()
    {
        if (!Directory.Exists(_root))
            return [];

        var backups = new List<SettingsBackup>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            if (_TryRead(directory) is { } backup)
                backups.Add(backup);
        }

        return backups.OrderByDescending(backup => backup.Manifest.CreatedAtUtc).ToList();
    }

    /// <summary>
    /// Writes a backup's files back over the profile it came from. The profile's current state is snapshotted first
    /// (<see cref="BackupReason.BeforeRestore"/>), so a restore is itself undoable. Files are matched by name, which
    /// is what keeps every character's settings with that character.
    /// </summary>
    public Result<SettingsRestoreOutcome> Restore(SettingsBackup backup, IReadOnlyDictionary<long, string> names)
    {
        if (!backup.CanRestore)
            return Result<SettingsRestoreOutcome>.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.ValidationFailed,
                $"This backup was written by a newer version of EVE Together (format {backup.Manifest.FormatVersion}) and cannot be restored here."));

        var profileDirectory = backup.Manifest.ProfileDirectory;
        if (!Directory.Exists(profileDirectory))
            return Result<SettingsRestoreOutcome>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                $"The profile this backup came from is gone: {profileDirectory}"));

        var safety = Create(
            EveSettingsLocator.LoadProfile(profileDirectory),
            backup.Manifest.InstallRoot,
            names,
            BackupReason.BeforeRestore,
            $"before restoring the backup of {_FormatTimestamp(backup.Manifest.CreatedAtUtc)}");
        if (!safety.IsSuccess)
            return Result<SettingsRestoreOutcome>.Failure(safety.Messages.ToArray());

        var restored = new List<string>();
        var failures = new List<string>();
        foreach (var entry in backup.Manifest.Entries)
        {
            var source = Path.Combine(backup.FilesDirectory, entry.FileName);
            if (!File.Exists(source))
            {
                failures.Add($"{entry.FileName}: missing from the backup");
                continue;
            }

            try
            {
                File.Copy(source, Path.Combine(profileDirectory, entry.FileName), overwrite: true);
                restored.Add(entry.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{entry.FileName}: {ex.Message}");
            }
        }

        return Result<SettingsRestoreOutcome>.Success(
            new SettingsRestoreOutcome(restored, failures, safety.Value?.DirectoryPath ?? string.Empty));
    }

    public Result Delete(SettingsBackup backup)
    {
        try
        {
            Directory.Delete(backup.DirectoryPath, recursive: true);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.FileIoFailed,
                $"Could not delete the backup: {ex.Message}"));
        }
    }

    private SettingsBackup? _TryRead(string directory)
    {
        try
        {
            var manifestPath = Path.Combine(directory, SettingsBackup.ManifestFileName);
            if (!File.Exists(manifestPath))
                return null;

            var manifest = JsonSerializer.Deserialize<SettingsBackupManifest>(File.ReadAllText(manifestPath), JsonOptions);
            return manifest is null ? null : new SettingsBackup(Path.GetFileName(directory), directory, manifest);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;   // an unreadable folder is not a backup we can offer to restore
        }
    }

    // Sortable, unique and readable in a file browser: 20260829-221430-settings_Default[-2].
    private string _NewBackupId(DateTimeOffset createdAt, string profileName)
    {
        var stem = $"{createdAt.ToLocalTime():yyyyMMdd-HHmmss}-{_Sanitize(profileName)}";
        var id = stem;
        var suffix = 2;
        while (Directory.Exists(Path.Combine(_root, id)))
            id = $"{stem}-{suffix++}";
        return id;
    }

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static string _Sanitize(string profileName) =>
        string.Concat(profileName.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c));

    private static string _FormatTimestamp(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static void _TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing better to do than leave the stub behind; the caller already reports the write failure.
        }
    }
}

/// <summary>What a restore did: which files went back, which did not, and where the pre-restore snapshot sits.</summary>
public sealed record SettingsRestoreOutcome(
    IReadOnlyList<string> Restored,
    IReadOnlyList<string> Failed,
    string SafetyBackupDirectory);
