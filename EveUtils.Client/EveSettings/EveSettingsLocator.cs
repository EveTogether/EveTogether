using System.Globalization;
using System.Text.RegularExpressions;

namespace EveUtils.Client.EveSettings;

/// <summary>
/// Finds EVE's settings on this machine: the install directory under <c>%LOCALAPPDATA%\CCP\EVE</c> and the
/// <c>settings_*</c> profiles inside it. On Windows that folder is where it says it is. On Linux EVE runs in a
/// Proton or Wine prefix, so the same folder sits inside one of those instead and <see cref="PrefixInstallRoot"/>
/// goes looking for it (ET-76). A failed detection is never a dead end — <see cref="LoadProfiles"/> works on any
/// directory the user picks, and the screen keeps that as its fallback.
/// </summary>
public static partial class EveSettingsLocator
{
    /// <summary>How close two write times must be to count as "written by the same client session" (see
    /// <see cref="DeriveAccountLinks"/>). EVE flushes a session's character and account file back to back on
    /// logout, well inside a minute; anything wider starts pulling unrelated sessions together.</summary>
    private static readonly TimeSpan SessionWriteWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How much further away the runner-up account must be before the nearest one counts as the answer. Logging in
    /// one character after another leaves pairs seconds apart, so this cannot be wide; closing six clients at once
    /// stamps every file the same second, and then the gap is zero and nothing is concluded — which is the point.
    /// </summary>
    private static readonly TimeSpan LinkSeparation = TimeSpan.FromSeconds(1.5);

    [GeneratedRegex(@"^core_char_(?<id>\d+)\.dat$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterFile();

    [GeneratedRegex(@"^core_user_(?<id>\d+)\.dat$", RegexOptions.IgnoreCase)]
    private static partial Regex AccountFile();

    /// <summary>EVE Online on Steam. Its Proton prefix is looked at first; a copy added to Steam by hand gets an id
    /// of its own, so the other prefixes are still searched behind it.</summary>
    private const string EveSteamAppId = "8500";

    /// <summary>Where Steam itself is installed, relative to the home directory. The last one is the Flatpak build,
    /// which keeps everything under its own sandbox and is what a Fedora install typically has.</summary>
    private static readonly string[] SteamHomeDirectories =
    [
        ".steam/steam",
        ".steam/root",
        ".local/share/Steam",
        ".var/app/com.valvesoftware.Steam/.local/share/Steam",
    ];

    [GeneratedRegex("\"path\"\\s*\"(?<path>[^\"]+)\"")]
    private static partial Regex LibraryFolderPath();

    /// <summary>
    /// The EVE install directory that holds the <c>settings_*</c> profiles, or null when there is none to find.
    /// Only installs that actually contain profiles count, and a Tranquility install wins over the others (Singularity
    /// and other test servers keep their own settings there under names of the same shape).
    /// </summary>
    public static string? DefaultInstallRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var here = string.IsNullOrEmpty(localAppData) ? null : _InstallUnder(localAppData);
        if (here is not null || !OperatingSystem.IsLinux())
            return here;

        // On Linux that folder was ~/.local/share/CCP/EVE, which EVE never writes: the Windows client it really is
        // writes into its Proton or Wine prefix instead.
        return PrefixInstallRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// The same search as <see cref="DefaultInstallRoot"/>, run over every Wine and Proton prefix reachable from a
    /// home directory (ET-76). Steam's libraries come from <c>libraryfolders.vdf</c>, because a game is as often on
    /// a second disk as in Steam's own folder, and inside a library the prefix is <c>steamapps/compatdata/&lt;app
    /// id&gt;/pfx</c>. <c>$WINEPREFIX</c> and <c>~/.wine</c> follow, for an install outside Steam altogether.
    ///
    /// <para>Nothing here is asked of Steam or Wine — it is directory names all the way down, and a prefix that
    /// holds no EVE settings simply does not answer. Taking <paramref name="home"/> as an argument is also what lets
    /// a test drive a whole fake Steam tree through it.</para>
    /// </summary>
    public static string? PrefixInstallRoot(string home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return null;

        return _PrefixLocalAppData(home).Select(_InstallUnder).FirstOrDefault(root => root is not null);
    }

    /// <summary>The EVE install under one <c>AppData\Local</c>, or null. This is the whole of the original Windows
    /// detection; on Linux it is simply asked more than once.</summary>
    private static string? _InstallUnder(string localAppData)
    {
        var eveRoot = Path.Combine(localAppData, "CCP", "EVE");
        if (!Directory.Exists(eveRoot))
            return null;

        var installs = _Directories(eveRoot)
            .Where(directory => _Directories(directory, "settings_*").Any())
            .ToList();

        return installs.FirstOrDefault(d => d.Contains("tranquility", StringComparison.OrdinalIgnoreCase))
               ?? installs.FirstOrDefault();
    }

    /// <summary>Every Windows <c>AppData\Local</c> a prefix under this home exposes. <c>steamuser</c> first — that is
    /// the account Proton runs games as — then any other user the prefix happens to carry.</summary>
    private static IEnumerable<string> _PrefixLocalAppData(string home)
    {
        foreach (var prefix in _Prefixes(home))
        {
            var users = Path.Combine(prefix, "drive_c", "users");
            foreach (var user in _Directories(users).OrderByDescending(
                         d => string.Equals(Path.GetFileName(d), "steamuser", StringComparison.OrdinalIgnoreCase)))
            {
                var localAppData = Path.Combine(user, "AppData", "Local");
                if (Directory.Exists(localAppData))
                    yield return localAppData;
            }
        }
    }

    private static IEnumerable<string> _Prefixes(string home)
    {
        foreach (var library in _SteamLibraries(home))
        {
            var compatData = Path.Combine(library, "steamapps", "compatdata");
            var eve = Path.Combine(compatData, EveSteamAppId, "pfx");
            if (Directory.Exists(eve))
                yield return eve;

            foreach (var other in _Directories(compatData))
                if (!string.Equals(Path.GetFileName(other), EveSteamAppId, StringComparison.Ordinal))
                    yield return Path.Combine(other, "pfx");
        }

        if (Environment.GetEnvironmentVariable("WINEPREFIX") is { Length: > 0 } winePrefix)
            yield return winePrefix;

        yield return Path.Combine(home, ".wine");
    }

    /// <summary>Steam's own folders plus every library listed in the first <c>libraryfolders.vdf</c> found — games
    /// live wherever the user put them, and that file is the only record of where.</summary>
    private static IEnumerable<string> _SteamLibraries(string home)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var relative in SteamHomeDirectories)
        {
            var steamRoot = Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(steamRoot))
                continue;

            foreach (var library in _LibraryFolders(steamRoot).Prepend(steamRoot))
                if (Directory.Exists(library) && seen.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(library))))
                    yield return library;
        }
    }

    // ponytail: libraryfolders.vdf read with a regex rather than a VDF parser — the file only ever has to give up
    // its "path" entries, and a parser (or a dependency for one) buys nothing else here.
    private static IEnumerable<string> _LibraryFolders(string steamRoot)
    {
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        try
        {
            if (!File.Exists(vdf))
                return [];

            return LibraryFolderPath().Matches(File.ReadAllText(vdf))
                .Select(match => match.Groups["path"].Value.Replace(@"\\", @"\"))
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Subdirectories, or none. Walking someone's whole home turns up folders that cannot be read and
    /// prefixes half-removed under our feet; neither is a reason to give up on the next candidate.</summary>
    private static IReadOnlyList<string> _Directories(string path, string pattern = "*")
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateDirectories(path, pattern).ToList() : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
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
    /// Which characters sit on which account, read out of the write times across <em>every</em> profile (ET-64).
    ///
    /// EVE exposes the link nowhere: it is not in <c>core_user_&lt;id&gt;.dat</c> (searched, as text and as a 32-bit
    /// number, with nothing to find — and the files are marshalled, not compressed, so there is nothing to unpack
    /// either), and there is no launcher database beside them. What EVE does do is write a session's character file
    /// and its account file within the same second when the client closes. That is the whole signal.
    ///
    /// Per character: take the account written closest to it. It counts as the answer only when it is inside
    /// <see cref="SessionWriteWindow"/> <em>and</em> the next-closest account is at least <see cref="LinkSeparation"/>
    /// further away. Logging characters in one at a time leaves pairs on the same second with seconds between the
    /// pairs, which passes easily; closing six clients at once stamps all twelve files identically, every account is
    /// equally close, and nothing is concluded. A wrong link is worse than none here — the user overwrites settings
    /// files on the strength of it.
    ///
    /// Looking at all profiles at once is what makes this work in practice: the operator's multiboxing profile
    /// carries no usable trace, while a quieter profile beside it holds a clean one-by-one login for the same
    /// accounts. An account belongs to a character regardless of which profile made that visible.
    /// </summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<long>> DeriveAccountLinks(
        IEnumerable<EveSettingsProfile> profiles)
    {
        var perCharacter = new Dictionary<long, long>();       // character → the account it was written beside
        var contradicted = new HashSet<long>();                // characters two profiles disagree about

        foreach (var profile in profiles)
        {
            foreach (var character in profile.Characters)
            {
                if (_NearestAccount(profile, character) is not { } accountId)
                    continue;

                if (perCharacter.TryGetValue(character.Id, out var already) && already != accountId)
                {
                    contradicted.Add(character.Id);   // two profiles, two answers: keep neither
                    continue;
                }

                perCharacter[character.Id] = accountId;
            }
        }

        var links = new Dictionary<long, List<long>>();
        foreach (var (characterId, accountId) in perCharacter.Where(pair => !contradicted.Contains(pair.Key)))
        {
            if (!links.TryGetValue(accountId, out var characters))
                links[accountId] = characters = [];
            characters.Add(characterId);
        }

        return links.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<long>)pair.Value.OrderBy(id => id).ToList());
    }

    /// <summary>The account this character was unmistakably written beside, or null when the write times cannot
    /// tell — no account close enough, or two of them equally close.</summary>
    private static long? _NearestAccount(EveSettingsProfile profile, EveSettingsFile character)
    {
        var byDistance = profile.Accounts
            .Select(account => (account.Id, Distance: (account.LastModifiedUtc - character.LastModifiedUtc).Duration()))
            .OrderBy(candidate => candidate.Distance)
            .ToList();

        if (byDistance.Count == 0 || byDistance[0].Distance > SessionWriteWindow)
            return null;

        if (byDistance.Count > 1 && byDistance[1].Distance - byDistance[0].Distance < LinkSeparation)
            return null;   // two clients closed together — nothing here says which account is whose

        return byDistance[0].Id;
    }

    /// <summary>
    /// Reads an EVE settings file name (<c>core_char_&lt;id&gt;.dat</c> / <c>core_user_&lt;id&gt;.dat</c>). False for
    /// anything else — the stubs EVE leaves behind, and, when a preset from another machine is opened, any name that
    /// is not one of these two shapes (ET-61): only files we recognise are ever unpacked over a profile.
    /// </summary>
    public static bool TryReadSettingsFileName(string fileName, out SettingsFileKind kind, out long id)
    {
        var characterMatch = CharacterFile().Match(fileName);
        if (characterMatch.Success && _TryReadId(characterMatch, out id))
        {
            kind = SettingsFileKind.Character;
            return true;
        }

        var accountMatch = AccountFile().Match(fileName);
        if (accountMatch.Success && _TryReadId(accountMatch, out id))
        {
            kind = SettingsFileKind.Account;
            return true;
        }

        kind = SettingsFileKind.Character;
        id = 0;
        return false;
    }

    private static bool _TryReadId(Match match, out long id) =>
        long.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id);

    private static EveSettingsFile _Describe(string path, long id, SettingsFileKind kind)
    {
        var info = new FileInfo(path);
        return new EveSettingsFile(path, id, kind, info.LastWriteTimeUtc, info.Length);
    }
}
