using System.Text.Json.Serialization;

namespace EveUtils.Client.EveSettings;

/// <summary>Whether a preset was meant as a deliberate subset or as the whole profile — shown when it is read back,
/// because "one account and one character" and "everything I had" are read very differently on the other side.</summary>
public enum PresetScope
{
    /// <summary>The characters and accounts the user picked. The main use: one well-set-up account and one
    /// well-set-up character are enough to build a new machine from.</summary>
    Selection,

    /// <summary>Every character and account in the profile.</summary>
    WholeProfile
}

/// <summary>
/// The wrapper around a <see cref="SettingsBackupManifest"/> that makes it portable (ET-61): the name the user gave
/// it, when and by which build it was written, and whether it is a subset or a whole profile. The manifest inside is
/// the same self-describing one a backup carries — deliberately the same format with a jacket on, rather than a
/// second one invented for travel.
///
/// What it does <em>not</em> carry is as deliberate: no absolute paths (the manifest's own directory fields are
/// blanked on the way out, since they spell the user's Windows account name), no tokens, no session state, nothing
/// about the machine. A preset is a file the user hands to somebody else.
/// </summary>
public sealed record PresetManifest
{
    /// <summary>Bumped when the shape changes; a preset from a newer build is described but not applied.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>The name the user chose ("default"), so several can be kept side by side.</summary>
    public required string Name { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>The EVE Together build that wrote it — a preset from an older one may hold older EVE settings.</summary>
    public required string AppVersion { get; init; }

    public required PresetScope Scope { get; init; }

    /// <summary>What is in it, by kind, id, name and write time: the backup manifest, paths removed.</summary>
    public required SettingsBackupManifest Contents { get; init; }

    [JsonIgnore]
    public bool IsKnownVersion =>
        FormatVersion <= CurrentFormatVersion && Contents.FormatVersion <= SettingsBackupManifest.CurrentFormatVersion;

    [JsonIgnore]
    public string ScopeSummary => Scope == PresetScope.WholeProfile
        ? $"the whole {Contents.ProfileName} profile"
        : $"a selection from {Contents.ProfileName}";
}

/// <summary>A preset file on disk: the manifest read out of it, plus where it lives.</summary>
public sealed record SettingsPreset(string FilePath, PresetManifest Manifest)
{
    /// <summary>The file extension presets are saved with — a zip holding <c>preset.json</c> and a <c>files/</c>
    /// folder, so it can be opened and checked with anything.</summary>
    public const string FileExtension = ".etpreset";

    public const string ManifestFileName = "preset.json";

    public const string FilesFolderName = "files";

    /// <summary>False for a preset written by a newer EVE Together: it is still described in full, but nothing from
    /// it is written over a profile. The same rule a backup follows.</summary>
    public bool CanApply => Manifest.IsKnownVersion;
}

/// <summary>What importing one entry from a preset would do to this machine.</summary>
public enum PresetImportAction
{
    /// <summary>Left alone.</summary>
    Skip,

    /// <summary>Written over a file that is already here.</summary>
    Overwrite,

    /// <summary>Written as a file this profile does not have yet — the normal case on a fresh EVE install, where
    /// hardly any <c>core_*.dat</c> exists until each character has logged in once.</summary>
    New
}

/// <summary>
/// One line of the import preview: what is in the preset, and where it would land. Kept as data rather than decided
/// inside the import so the whole thing can be shown, and changed, before a byte is written.
/// </summary>
public sealed record PresetImportItem(
    SettingsBackupEntry Entry,
    PresetImportAction Action,
    EveSettingsFile? Target,
    string TargetFileName,
    string TargetLabel);

/// <summary>Where a preset is being written and what happens to each of its entries.</summary>
public sealed record PresetImportPlan(
    EveSettingsProfile Profile,
    string InstallRoot,
    IReadOnlyList<PresetImportItem> Items);

/// <summary>What an import did, split the way the preview promised it: overwritten, newly written, skipped, failed —
/// and the backup taken before any of it.</summary>
public sealed record PresetImportOutcome(
    IReadOnlyList<string> Overwritten,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Failed,
    SettingsBackup Backup);
