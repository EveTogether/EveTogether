using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveUtils.Client.EveSettings;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One backup in the list: when it was taken, of which profile, why, and what is in it — by name, so restoring is a
/// decision the user can actually make rather than a leap of faith.
/// </summary>
public sealed class SettingsBackupRowViewModel(SettingsBackup backup) : ViewModelBase
{
    public SettingsBackup Backup { get; } = backup;

    public string TakenAtDisplay =>
        Backup.Manifest.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string ProfileName => Backup.Manifest.ProfileName;

    public string ReasonDisplay => Backup.Manifest.Reason switch
    {
        BackupReason.BeforeSync => "before a sync",
        BackupReason.BeforeRestore => "before a restore",
        _ => "made by hand"
    };

    public string ContentsDisplay =>
        $"{Backup.Manifest.CharacterCount} characters · {Backup.Manifest.AccountCount} accounts · {Backup.TotalSizeBytes / 1024d / 1024d:0.0} MB";

    public string Note => Backup.Manifest.Note;

    public bool CanRestore => Backup.CanRestore;

    /// <summary>The names inside, for the detail panel: characters first, then accounts.</summary>
    public IReadOnlyList<string> Contents => Backup.Manifest.Entries
        .OrderBy(entry => entry.Kind)
        .ThenBy(entry => entry.Name, System.StringComparer.OrdinalIgnoreCase)
        .Select(entry => string.IsNullOrWhiteSpace(entry.Name)
            ? $"{_KindLabel(entry.Kind)} {entry.Id}"
            : $"{_KindLabel(entry.Kind)} · {entry.Name}")
        .ToList();

    private static string _KindLabel(SettingsFileKind kind) =>
        kind == SettingsFileKind.Character ? "Character" : "Account";
}
