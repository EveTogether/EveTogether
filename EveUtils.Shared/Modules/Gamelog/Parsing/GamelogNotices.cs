using System.Globalization;
using EveUtils.Shared.Modules.Gamelog.Languages;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Parsing;

/// <summary>Reads the two notify lines the client acts on, in the language the line is written in.</summary>
public static class GamelogNotices
{
    /// <summary>The line EVE writes when a mining module finds its asteroid emptied — the one hard local signal a
    /// Metaliminal Meteoroid site completed (ET-262).</summary>
    public static bool IsResourceDepleted(string message, GamelogLanguage language) =>
        GamelogGrammar.For(language)?.ResourceDepleted.IsMatch(message) ?? false;

    /// <summary>The line a command-burst booster's own log writes once per burst module per cycle (ET-283): the
    /// module, and how many fleet members it reached.</summary>
    public static bool TryReadMiningBoost(string message, GamelogLanguage language, out string module, out int count)
    {
        module = string.Empty;
        count = 0;
        if (GamelogGrammar.For(language)?.MiningBoost.Match(message) is not { Success: true } boost)
        {
            return false;
        }

        module = boost.Groups["module"].Value;
        return int.TryParse(boost.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
    }
}
