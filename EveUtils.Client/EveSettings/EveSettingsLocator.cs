using System.Globalization;
using System.Text.RegularExpressions;

namespace EveUtils.Client.EveSettings;

/// <summary>
/// Finds EVE's settings on this machine: the install directory under <c>%LOCALAPPDATA%\CCP\EVE</c> and the
/// <c>settings_*</c> profiles inside it. Windows auto-detects; on Linux/macOS the settings live in a Wine/Proton
/// prefix, so nothing is guessed there and the caller lets the user point at the directory instead. A failed
/// detection is never a dead end — <see cref="LoadProfiles"/> works on any directory the user picks.
/// </summary>
public static partial class EveSettingsLocator
{
    /// <summary>How close two write times must be to count as "written by the same client session" (see
    /// <see cref="AccountCharacterHints"/>). EVE flushes a session's character and account file back to back on
    /// logout, well inside a minute; anything wider starts pulling unrelated sessions together.</summary>
    private static readonly TimeSpan SessionWriteWindow = TimeSpan.FromSeconds(60);

    [GeneratedRegex(@"^core_char_(?<id>\d+)\.dat$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterFile();

    [GeneratedRegex(@"^core_user_(?<id>\d+)\.dat$", RegexOptions.IgnoreCase)]
    private static partial Regex AccountFile();

    /// <summary>
    /// The EVE install directory that holds the <c>settings_*</c> profiles, or null when there is none to find.
    /// Only installs that actually contain profiles count, and a Tranquility install wins over the others (Singularity
    /// and other test servers keep their own settings there under names of the same shape).
    /// </summary>
    public static string? DefaultInstallRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
            return null;

        var eveRoot = Path.Combine(localAppData, "CCP", "EVE");
        if (!Directory.Exists(eveRoot))
            return null;

        List<string> installs;
        try
        {
            installs = Directory.EnumerateDirectories(eveRoot)
                .Where(directory => Directory.EnumerateDirectories(directory, "settings_*").Any())
                .ToList();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return installs.FirstOrDefault(d => d.Contains("tranquility", StringComparison.OrdinalIgnoreCase))
               ?? installs.FirstOrDefault();
    }

    /// <summary>Every <c>settings_*</c> profile under an install directory, by name. Empty when the directory does
    /// not exist or holds no profiles — the caller shows that as "nothing found here", not as an error.</summary>
    public static IReadOnlyList<EveSettingsProfile> LoadProfiles(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return [];

        return Directory.EnumerateDirectories(installRoot, "settings_*")
            .Select(LoadProfile)
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads one profile directory. Only <c>core_char_&lt;id&gt;.dat</c> and <c>core_user_&lt;id&gt;.dat</c> are
    /// picked up: a real profile also holds stubs like <c>core_char__.dat</c> that carry no character, and those are
    /// neither a source nor a target.
    /// </summary>
    public static EveSettingsProfile LoadProfile(string profileDirectory)
    {
        var characters = new List<EveSettingsFile>();
        var accounts = new List<EveSettingsFile>();

        foreach (var path in Directory.EnumerateFiles(profileDirectory, "core_*.dat"))
        {
            var fileName = Path.GetFileName(path);

            var characterMatch = CharacterFile().Match(fileName);
            if (characterMatch.Success && _TryReadId(characterMatch, out var characterId))
            {
                characters.Add(_Describe(path, characterId, SettingsFileKind.Character));
                continue;
            }

            var accountMatch = AccountFile().Match(fileName);
            if (accountMatch.Success && _TryReadId(accountMatch, out var accountId))
                accounts.Add(_Describe(path, accountId, SettingsFileKind.Account));
        }

        return new EveSettingsProfile(
            Path.GetFileName(profileDirectory),
            profileDirectory,
            characters.OrderBy(file => file.Id).ToList(),
            accounts.OrderBy(file => file.Id).ToList());
    }

    /// <summary>
    /// Best guess at which characters live on which account, from the write times of the files. EVE exposes no link
    /// between an account id and its characters, but it does write a session's character file and its account file
    /// within seconds of each other when the client closes. So: bucket the files by write time, and when a bucket
    /// holds exactly one account, the characters in it were on that account. Buckets with several accounts (two
    /// clients closed together) prove nothing and are dropped rather than guessed at — this is a hint the user sees
    /// while naming an account, never a fact the sync acts on.
    /// </summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<long>> AccountCharacterHints(EveSettingsProfile profile)
    {
        var hints = new Dictionary<long, IReadOnlyList<long>>();

        foreach (var account in profile.Accounts)
        {
            if (profile.Accounts.Any(other => other.Id != account.Id && _WrittenTogether(other, account)))
                continue;   // two accounts written at once — no way to tell whose characters are whose

            var characters = profile.Characters
                .Where(character => _WrittenTogether(character, account))
                .Select(character => character.Id)
                .ToList();

            if (characters.Count > 0)
                hints[account.Id] = characters;
        }

        return hints;
    }

    private static bool _WrittenTogether(EveSettingsFile left, EveSettingsFile right) =>
        (left.LastModifiedUtc - right.LastModifiedUtc).Duration() <= SessionWriteWindow;

    private static bool _TryReadId(Match match, out long id) =>
        long.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id);

    private static EveSettingsFile _Describe(string path, long id, SettingsFileKind kind)
    {
        var info = new FileInfo(path);
        return new EveSettingsFile(path, id, kind, info.LastWriteTimeUtc, info.Length);
    }
}
