using System;
using System.IO;
using System.Linq;
using EveUtils.Client.EveSettings;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Finding EVE on Linux (ET-76). The client is the Windows one running under Proton, so its settings sit in a
/// prefix — <c>steamapps/compatdata/8500/pfx/drive_c/users/steamuser/AppData/Local/CCP/EVE/...</c> — and Steam's
/// libraries are wherever <c>libraryfolders.vdf</c> says. Everything here runs against a throwaway home directory,
/// never the machine's own Steam. The last two tests are the other half of the ticket: detection coming up empty
/// has to be an ordinary answer, because the user then points at the folder by hand.
/// </summary>
public sealed class EveSettingsLinuxDetectionTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "eveutils-home-" + Guid.NewGuid().ToString("N"));

    private const string FlatpakSteam = ".var/app/com.valvesoftware.Steam/.local/share/Steam";
    private const string NativeSteam = ".local/share/Steam";

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Scratch files; a leftover temp directory is harmless.
        }
    }

    private string _SteamRoot(string relative)
    {
        var root = Path.Combine(_home, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        return root;
    }

    /// <summary>A Proton prefix for one Steam app id inside a library.</summary>
    private static string _Prefix(string library, string appId)
    {
        var prefix = Path.Combine(library, "steamapps", "compatdata", appId, "pfx");
        Directory.CreateDirectory(prefix);
        return prefix;
    }

    /// <summary>An EVE install inside a prefix, with a settings profile in it unless <paramref name="profile"/> is
    /// null — an install without one is not a place to sync from and must not be picked.</summary>
    private static string _EveInstall(string prefix, string installName, string? profile = "settings_Default",
        string user = "steamuser")
    {
        var install = Path.Combine(prefix, "drive_c", "users", user, "AppData", "Local", "CCP", "EVE", installName);
        Directory.CreateDirectory(profile is null ? install : Path.Combine(install, profile));
        return install;
    }

    /// <summary>The real file is VDF; only its "path" entries matter here, so the shape is copied and nothing else.</summary>
    private static void _WriteLibraryFolders(string steamRoot, params string[] libraries)
    {
        var entries = libraries.Select((path, index) =>
            $"\t\"{index}\"\n\t{{\n\t\t\"path\"\t\t\"{path}\"\n\t\t\"label\"\t\t\"\"\n\t}}");
        File.WriteAllText(
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n" + string.Join("\n", entries) + "\n}\n");
    }

    /// <summary>The ordinary case on this operator's machine: Flatpak Steam, EVE under app id 8500, one profile.</summary>
    [Fact]
    public void PrefixInstallRoot_FindsEveInItsProtonPrefix()
    {
        var steam = _SteamRoot(FlatpakSteam);
        var expected = _EveInstall(_Prefix(steam, "8500"), "c_ccp_eve_tq_tranquility");

        Assert.Equal(expected, EveSettingsLocator.PrefixInstallRoot(_home));
    }

    /// <summary>Games are as often on a second disk as in Steam's own folder, and libraryfolders.vdf is the only
    /// record of where that disk is.</summary>
    [Fact]
    public void PrefixInstallRoot_FollowsLibraryFoldersToASecondLibrary()
    {
        var steam = _SteamRoot(NativeSteam);
        var elsewhere = Path.Combine(_home, "games", "SteamLibrary");
        Directory.CreateDirectory(elsewhere);
        _WriteLibraryFolders(steam, steam, elsewhere);

        var expected = _EveInstall(_Prefix(elsewhere, "8500"), "c_ccp_eve_tq_tranquility");

        Assert.Equal(expected, EveSettingsLocator.PrefixInstallRoot(_home));
    }

    /// <summary>The same two rules the Windows detection has always had, now applied inside a prefix: an install
    /// with no settings_* folder is not an answer, and Tranquility beats a test server.</summary>
    [Fact]
    public void PrefixInstallRoot_SkipsAnInstallWithoutProfiles_AndPrefersTranquility()
    {
        var prefix = _Prefix(_SteamRoot(FlatpakSteam), "8500");
        _EveInstall(prefix, "c_ccp_eve_tq_tranquility_empty", profile: null);
        _EveInstall(prefix, "c_ccp_eve_sisi_singularity");
        var expected = _EveInstall(prefix, "c_ccp_eve_tq_tranquility");

        Assert.Equal(expected, EveSettingsLocator.PrefixInstallRoot(_home));
    }

    /// <summary>EVE added to Steam by hand gets an app id of its own, so the other prefixes are searched too.</summary>
    [Fact]
    public void PrefixInstallRoot_SearchesOtherAppIdsWhen8500HoldsNothing()
    {
        var steam = _SteamRoot(FlatpakSteam);
        _EveInstall(_Prefix(steam, "8500"), "c_ccp_eve_tq_tranquility", profile: null);
        var expected = _EveInstall(_Prefix(steam, "4197610"), "c_ccp_eve_tq_tranquility");

        Assert.Equal(expected, EveSettingsLocator.PrefixInstallRoot(_home));
    }

    /// <summary>Not everyone runs EVE through Steam. A plain Wine prefix is named after the login user rather than
    /// steamuser, which is why the user folder is read rather than assumed.</summary>
    [Fact]
    public void PrefixInstallRoot_FindsAPlainWinePrefix()
    {
        var expected = _EveInstall(Path.Combine(_home, ".wine"), "c_ccp_eve_tq_tranquility", user: "jithran");

        Assert.Equal(expected, EveSettingsLocator.PrefixInstallRoot(_home));
    }

    /// <summary>Steam installed, EVE not — the answer is "nothing found", not an exception and not a guess. This is
    /// the case the manual BROWSE… path exists for.</summary>
    [Fact]
    public void PrefixInstallRoot_ReturnsNullWhenNoPrefixHoldsEve()
    {
        var steam = _SteamRoot(FlatpakSteam);
        _Prefix(steam, "1284210");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "compatdata", "228980", "pfx",
            "drive_c", "users", "steamuser", "AppData", "Local"));

        Assert.Null(EveSettingsLocator.PrefixInstallRoot(_home));
    }

    /// <summary>A home with no Steam and no Wine at all: still an answer, still no exception.</summary>
    [Fact]
    public void PrefixInstallRoot_ReturnsNullOnABareHome()
    {
        Directory.CreateDirectory(_home);

        Assert.Null(EveSettingsLocator.PrefixInstallRoot(_home));
        Assert.Null(EveSettingsLocator.PrefixInstallRoot(string.Empty));
    }

    /// <summary>Whatever detection returns, the profiles under it are read by the same code that reads a folder the
    /// user picked by hand — the detected path is a shortcut to the manual one, not a second way in.</summary>
    [Fact]
    public void LoadProfiles_ReadsADetectedPrefixInstallLikeAnyOtherFolder()
    {
        var install = _EveInstall(_Prefix(_SteamRoot(FlatpakSteam), "8500"), "c_ccp_eve_tq_tranquility");
        File.WriteAllText(Path.Combine(install, "settings_Default", "core_char_90000001.dat"), "alice");

        var detected = EveSettingsLocator.PrefixInstallRoot(_home);
        Assert.Equal(install, detected);

        var profiles = EveSettingsLocator.LoadProfiles(detected!);
        Assert.Equal(["settings_Default"], profiles.Select(profile => profile.Name));
        Assert.Equal([90000001L], profiles[0].Characters.Select(file => file.Id));
    }
}
