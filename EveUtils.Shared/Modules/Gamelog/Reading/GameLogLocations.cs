using System.Runtime.InteropServices;

namespace EveUtils.Shared.Modules.Gamelog.Reading;

/// <summary>
/// Best-effort default EVE gamelog directory for the current OS. EVE writes gamelogs to
/// <c>Documents/EVE/logs/Gamelogs</c>; on Linux that lives inside a Wine/Proton prefix, so we probe the
/// common Steam-Proton (EVE = Steam appid 8500) and Wine locations. There is no fully reliable default on
/// Linux, so callers should let the user override the path (settings: <c>gamelog.directory</c>).
/// </summary>
public static class GameLogLocations
{
    private const string EveSteamAppId = "8500";
    private static readonly string[] DocumentsTail = ["Documents", "EVE", "logs", "Gamelogs"];

    /// <summary>The first candidate directory that exists, or the platform's nominal default when none do yet.</summary>
    public static string Default()
    {
        var candidates = Candidates().ToList();
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    /// <summary>Candidate gamelog directories for the current OS, most-likely first.</summary>
    public static IEnumerable<string> Candidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return NativeDocuments();
            yield break;
        }

        // Linux/macOS: EVE runs under Wine/Proton, so the logs sit inside a prefix's drive_c.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            // Steam Proton prefix for EVE (appid 8500), native install.
            yield return ProtonPrefix(Path.Combine(home, ".local", "share", "Steam"));
            // Steam Flatpak.
            yield return ProtonPrefix(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
            // Plain Wine default prefix (drive_c/users/<user>).
            yield return Combine(Path.Combine(home, ".wine", "drive_c", "users", Environment.UserName), DocumentsTail);
        }

        // Fallback to the nominal Documents path (native macOS, or when nothing above matched).
        yield return NativeDocuments();
    }

    private static string ProtonPrefix(string steamRoot) =>
        Combine(Path.Combine(steamRoot, "steamapps", "compatdata", EveSteamAppId, "pfx", "drive_c", "users", "steamuser"), DocumentsTail);

    private static string NativeDocuments()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents))
            return Combine(documents, ["EVE", "logs", "Gamelogs"]);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Combine(home, DocumentsTail);
    }

    private static string Combine(string root, string[] tail) => Path.Combine([root, .. tail]);
}
