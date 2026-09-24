using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Languages;

/// <summary>
/// The regexes for one language, built at runtime from its <see cref="GamelogTemplates"/> and cached per language.
/// A deliberate departure from the <c>[GeneratedRegex]</c> used elsewhere in this code base: a source-generated regex
/// is a compile-time pattern, and these patterns are data that differ per language. They are built once, on the first
/// line read in that language, never per line.
/// </summary>
internal sealed class GamelogGrammar
{
    // Integers the game writes without grouping; a separator is tolerated because how each client language groups
    // (if it does) could not be confirmed against a real non-English log, and the digits alone are the value.
    private const string GroupSeparators = @"[.,'’  ]";
    private const string Amount = @"[-+]?\d+(?:" + GroupSeparators + @"\d{3})*";

    // A payout is grouped in the client's own language, may carry a 1-2 digit fraction that whole-ISK totals drop
    // (ET-41), and is followed by the currency the game formats into the number ("ISK").
    private const string Isk = @"(?<isk>\d{1,3}(?:" + GroupSeparators + @"\d{3})*|\d+)(?:[.,]\d{1,2})?(?:\s?\p{Lu}{3})?";

    private static readonly Regex TokenMarker = new(@"\{([^{}]+)\}", RegexOptions.CultureInvariant);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex CjkCharacter = new(@"[぀-ヿ㐀-鿿가-힯]", RegexOptions.CultureInvariant);

    private static readonly IReadOnlySet<string> NamedText = new HashSet<string>
    {
        "ore", "weapon", "target", "source", "owner", "module", "gate", "station", "system"
    };

    private static readonly ConcurrentDictionary<GamelogLanguage, GamelogGrammar?> Cache = new();

    private GamelogGrammar(GamelogTemplates t)
    {
        OutgoingMisses = [_Compile(t.MissOutGroup, withTail: true), _Compile(t.MissOutSingle, withTail: true)];
        IncomingMisses = [_Compile(t.MissInOwned, withTail: true), _Compile(t.MissInSource, withTail: true)];

        Repairs =
        [
            new(_Compile(t.ArmorRepairTo), LineDirection.Outgoing, "armor"),
            new(_Compile(t.ArmorRepairBy), LineDirection.Incoming, "armor"),
            new(_Compile(t.ShieldBoostTo), LineDirection.Outgoing, "shield"),
            new(_Compile(t.ShieldBoostBy), LineDirection.Incoming, "shield"),
            new(_Compile(t.HullRepairTo), LineDirection.Outgoing, "hull"),
            new(_Compile(t.HullRepairBy), LineDirection.Incoming, "hull")
        ];
        CapTransfers =
        [
            new(_Compile(t.CapacitorTransmittedTo), LineDirection.Outgoing),
            new(_Compile(t.CapacitorTransmittedBy), LineDirection.Incoming)
        ];

        // Some languages write the same words for both directions of a neut (the sign of the amount and the line's
        // colour tell them apart); the others word each direction differently.
        Neutralizers = t.EnergyNeutralizedOut == t.EnergyNeutralizedIn
            ? [new(_Compile(t.EnergyNeutralizedOut), LineDirection.ByColour)]
            :
            [
                new(_Compile(t.EnergyNeutralizedOut), LineDirection.Outgoing),
                new(_Compile(t.EnergyNeutralizedIn), LineDirection.Incoming)
            ];

        string spacing = _WordsNeedSpaces(t.DirectionTo) && _WordsNeedSpaces(t.DirectionFrom) ? @"\s+" : @"\s*";
        DamageHead = new Regex(
            $@"^(?<amount>{Amount}){spacing}(?:(?<to>{Regex.Escape(t.DirectionTo)})|(?<from>{Regex.Escape(t.DirectionFrom)})){spacing}(?<target>.+)$",
            RegexOptions.CultureInvariant);
        DirectionByColour = t.DirectionTo == t.DirectionFrom;

        Qualities = new Dictionary<string, HitQuality>(StringComparer.Ordinal)
        {
            [t.Hits] = HitQuality.Hits,
            [t.Penetrates] = HitQuality.Penetrates,
            [t.Grazes] = HitQuality.Grazes,
            [t.Smashes] = HitQuality.Smashes,
            [t.GlancesOff] = HitQuality.Glances,
            [t.Wrecks] = HitQuality.Wrecks
        };

        Mined = _Compile(t.Mined);
        CriticalMined = _Compile(t.CriticalMined);
        MiningResidue = _Compile(t.MiningResidue);
        Bounty = _Compile(t.Bounty);
        Jumping = _Compile(t.Jumping);
        Undocking = _Compile(t.Undocking);
        LocationMarkers = [_LongestWord(t.Jumping), _LongestWord(t.Undocking)];
        ResourceDepleted = _Compile(t.ResourceDepleted);
        MiningBoost = _Compile(t.MiningBoost);
    }

    /// <summary>The grammar of <paramref name="language"/>, or null for a language this build has no templates for.</summary>
    public static GamelogGrammar? For(GamelogLanguage language) =>
        Cache.GetOrAdd(language, key => GamelogTemplateTables.All.TryGetValue(key, out GamelogTemplates? templates)
            ? new GamelogGrammar(templates)
            : null);

    public IReadOnlyList<Regex> OutgoingMisses { get; }
    public IReadOnlyList<Regex> IncomingMisses { get; }
    public IReadOnlyList<DirectedPattern> Repairs { get; }
    public IReadOnlyList<DirectedPattern> CapTransfers { get; }
    public IReadOnlyList<DirectedPattern> Neutralizers { get; }
    public Regex DamageHead { get; }
    public bool DirectionByColour { get; }
    public IReadOnlyDictionary<string, HitQuality> Qualities { get; }
    public Regex Mined { get; }
    public Regex CriticalMined { get; }
    public Regex MiningResidue { get; }
    public Regex Bounty { get; }
    public Regex Jumping { get; }
    public Regex Undocking { get; }
    public Regex ResourceDepleted { get; }
    public Regex MiningBoost { get; }

    /// <summary>A word every location line of this language contains — the cheap test that keeps a scan for the last
    /// known location from parsing every line of a large log.</summary>
    public IReadOnlyList<string> LocationMarkers { get; }

    private static Regex _Compile(string template, bool withTail = false)
    {
        StringBuilder pattern = new("^");
        int position = 0;
        foreach (Match token in TokenMarker.Matches(template))
        {
            _AppendLiteral(pattern, template[position..token.Index]);
            pattern.Append(_TokenPattern(token.Groups[1].Value));
            position = token.Index + token.Length;
        }

        // The game ends some sentences with a full stop the log line may or may not carry.
        string closing = template[position..];
        bool endsWithPeriod = closing.EndsWith('.');
        _AppendLiteral(pattern, endsWithPeriod ? closing[..^1] : closing);
        if (endsWithPeriod)
        {
            pattern.Append(@"\.?");
        }

        // A miss line carries its weapon after the sentence: "... completely - Mega Pulse Laser II".
        if (withTail)
        {
            pattern.Append(@"(?<tail>\s+-\s+.+)?");
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant);
    }

    private static string _TokenPattern(string token)
    {
        if (token.Contains('|'))
        {
            return "(?:" + string.Join('|', token.Split('|').Select(Regex.Escape)) + ")";
        }

        return token switch
        {
            "amount" => $"(?<amount>{Amount})",
            "isk" => Isk,
            "count" => @"(?<count>\d+)",
            "rest" => "(?<rest>.+)",
            _ when NamedText.Contains(token) => $"(?<{token}>.+?)",
            _ => throw new InvalidOperationException($"Unknown gamelog template token '{{{token}}}'")
        };
    }

    private static void _AppendLiteral(StringBuilder pattern, string literal) =>
        pattern.AppendJoin(@"\s+", WhitespaceRun.Split(literal).Select(Regex.Escape));

    private static bool _WordsNeedSpaces(string word) => !CjkCharacter.IsMatch(word);

    private static string _LongestWord(string template) =>
        WhitespaceRun.Split(TokenMarker.Replace(template, " "))
            .OrderByDescending(word => word.Length)
            .First();
}

internal enum LineDirection
{
    Outgoing,
    Incoming,
    ByColour
}

internal sealed record DirectedPattern(Regex Regex, LineDirection Direction, string? Kind = null);
