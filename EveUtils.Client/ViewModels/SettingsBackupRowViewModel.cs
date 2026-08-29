using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveUtils.Client.EveSettings;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One backup in the list: when it was taken, of which profile, why, and what is in it — by name, so restoring is a
/// decision the user can actually make rather than a leap of faith. Characters and accounts are kept as two named
/// groups: a backup covers the whole profile, and that has to be visible without reading the code.
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

    public string ContentsDisplay => $"{Backup.Manifest.ContentsSummary} · {_SizeDisplay()}";

    // KB below a megabyte: "0.0 MB" on a small profile reads as "nothing was saved", which is the opposite of what
    // this panel is for.
    private string _SizeDisplay() => Backup.TotalSizeBytes < 1024 * 1024
        ? (Backup.TotalSizeBytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB"
        : (Backup.TotalSizeBytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    public string Note => Backup.Manifest.Note;

    public bool CanRestore => Backup.CanRestore;

    public string CharacterHeader => $"CHARACTERS ({Backup.Manifest.CharacterCount})";

    public string AccountHeader => $"ACCOUNTS ({Backup.Manifest.AccountCount})";

    /// <summary>The characters in the backup, by name with their id beside it.</summary>
    public IReadOnlyList<string> CharacterContents => _Entries(SettingsFileKind.Character);

    /// <summary>The accounts in the backup — listed separately so they can never scroll out of sight behind a long
    /// character list, which is exactly how a complete backup came to look like a character-only one.</summary>
    public IReadOnlyList<string> AccountContents => _Entries(SettingsFileKind.Account);

    // The manifest keeps only names it really had, so a preset built on it later carries no invented ones; an entry
    // that never resolved is labelled here instead of listed as a bare number.
    private IReadOnlyList<string> _Entries(SettingsFileKind kind) => Backup.Manifest.Entries
        .Where(entry => entry.Kind == kind)
        .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        .Select(entry => string.IsNullOrWhiteSpace(entry.Name)
            ? $"{(kind == SettingsFileKind.Character ? "Character" : "Account")} {entry.Id.ToString(CultureInfo.InvariantCulture)}"
            : $"{entry.Name} · {entry.Id.ToString(CultureInfo.InvariantCulture)}")
        .ToList();
}
