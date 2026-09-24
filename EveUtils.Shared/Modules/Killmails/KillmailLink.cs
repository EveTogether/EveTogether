using System.Globalization;
using System.Text.RegularExpressions;

namespace EveUtils.Shared.Modules.Killmails;

/// <summary>
/// Pulls a killmail id and hash out of a pasted ESI killmail link or an in-game <c>killReport:</c> chat link
/// (ET-338) — pure text work, found anywhere in the pasted text rather than requiring the whole input to be the
/// link. Fetching the killmail is <c>EsiKillmailImporter.ImportOneAsync</c>'s job.
/// </summary>
public static partial class KillmailLink
{
    // Requiring the literal host right after the scheme separator keeps a foreign host that happens to carry the
    // same id/hash shape (e.g. zKillboard, which carries no hash at all) from being read as an ESI link.
    [GeneratedRegex(@"//esi\.evetech\.net/(?:(?:latest|dev|legacy|v\d+)/)?killmails/(\d+)/([0-9a-f]{40})",
        RegexOptions.IgnoreCase)]
    private static partial Regex EsiLinkShape();

    [GeneratedRegex(@"killReport:(\d+):([0-9a-f]{40})", RegexOptions.IgnoreCase)]
    private static partial Regex ChatLinkShape();

    /// <summary>
    /// True with the id and hash when <paramref name="text"/> contains an ESI killmail link or a
    /// <c>killReport:</c> chat link anywhere in it; false otherwise (an unrecognised link or free text), with no
    /// ESI call ever attempted for it.
    /// </summary>
    public static bool TryParse(string text, out int killmailId, out string hash)
    {
        killmailId = 0;
        hash = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        Match match = EsiLinkShape().Match(text);
        if (!match.Success)
        {
            match = ChatLinkShape().Match(text);
        }
        if (!match.Success)
        {
            return false;
        }

        killmailId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        hash = match.Groups[2].Value;
        return true;
    }
}
