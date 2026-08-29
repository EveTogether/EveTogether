namespace EveUtils.Client.EveSettings;

/// <summary>
/// Which half of EVE's client settings a <c>.dat</c> file holds. The two never mix: a character file carries the
/// window layout, overview and tab setup of one pilot, an account file the settings that apply to every pilot on
/// one login. Copying one over the other corrupts both, so the kind travels with every file and target list.
/// </summary>
public enum SettingsFileKind
{
    /// <summary><c>core_char_&lt;id&gt;.dat</c> — one character.</summary>
    Character,

    /// <summary><c>core_user_&lt;id&gt;.dat</c> — one account (all characters on it).</summary>
    Account
}

/// <summary>One EVE settings file inside a profile directory, described well enough to show and to copy.</summary>
public sealed record EveSettingsFile(
    string FullPath,
    long Id,
    SettingsFileKind Kind,
    DateTimeOffset LastModifiedUtc,
    long SizeBytes)
{
    public string FileName => Path.GetFileName(FullPath);
}

/// <summary>
/// One <c>settings_*</c> profile directory with its character and account files split apart. Several profiles side
/// by side is normal (EVE writes one per profile the launcher offers), so the profile is never implicit: every
/// action names the profile it acts on.
/// </summary>
public sealed record EveSettingsProfile(
    string Name,
    string DirectoryPath,
    IReadOnlyList<EveSettingsFile> Characters,
    IReadOnlyList<EveSettingsFile> Accounts);
