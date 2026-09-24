using System.Globalization;
using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Gamelog.Languages;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Parsing;

/// <summary>
/// Parses EVE gamelog lines into <see cref="GameLogEvent"/>s. Folded from the EVE-Utils demo (own code). The words of a
/// line come from the language table of the file's client language (<see cref="GamelogGrammar"/>); the framing — the
/// timestamp, the category, the markup — is the same in every language.
/// </summary>
public static partial class LogLineParser
{
    private const string TimestampFormat = "yyyy.MM.dd HH:mm:ss";
    private const string ModuleSeparator = " - ";

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^\[ (?<ts>\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2}) \] \((?<cat>\w+)\) (?<body>.*)$")]
    private static partial Regex LinePrefix();

    // The line's leading colour tag (the amount's colour) — EVE encodes energy-warfare direction here, not in the text.
    [GeneratedRegex(@"^<color=0x(?<hex>[0-9a-fA-F]{8})>")]
    private static partial Regex LeadColor();

    // Neut direction by lead colour, validated against real gamelogs + the cono reference (erstschlag/cono, reference
    // only — own implementation): incoming neut (applied to you) = 0xffe57f7f, outgoing neut (you neuting a target) =
    // 0xff7fffff. Any other colour on an "energy neutralized" line is treated as outgoing (only these two are emitted).
    private const string IncomingNeutColor = "ffe57f7f";

    // Damage lines carry their direction in the colour too: 0xff00ffff going out, 0xffcc0000 coming in. It only
    // decides for a language that words both directions alike (Japanese "から" for to and from).
    private const string OutgoingDamageColor = "ff00ffff";
    private const string IncomingDamageColor = "ffcc0000";

    public static LogCategory ParseCategory(string raw) => raw.ToLowerInvariant() switch
    {
        "combat" => LogCategory.Combat,
        "mining" => LogCategory.Mining,
        "notify" => LogCategory.Notify,
        "hint" => LogCategory.Hint,
        "bounty" => LogCategory.Bounty,
        "question" => LogCategory.Question,
        "info" => LogCategory.Info,
        "warning" => LogCategory.Warning,
        "none" => LogCategory.None,
        _ => LogCategory.Unknown
    };

    public static string StripTags(string text) => HtmlTag().Replace(text, string.Empty).Trim();

    /// <summary>The other end of a rep, cap or neut line, from its "&lt;counterparty&gt; - &lt;module&gt;" rest. The
    /// counterparty (ship, tickers, fit title) can itself contain " - ", so this anchors on the module at the end
    /// instead of splitting at the first separator (ET-321).</summary>
    public static string CounterpartyOf(string rest)
    {
        int moduleSeparator = rest.LastIndexOf(ModuleSeparator, StringComparison.Ordinal);
        return moduleSeparator < 0 ? rest : rest[..moduleSeparator];
    }

    public static GameLogEvent? Parse(string line) => Parse(line, GamelogLanguage.English);

    /// <summary>Parses one line of a gamelog written in <paramref name="language"/>; a language without templates
    /// (<see cref="GamelogLanguage.Unknown"/>) reads nothing.</summary>
    public static GameLogEvent? Parse(string line, GamelogLanguage language)
    {
        if (GamelogGrammar.For(language) is not { } grammar)
        {
            return null;
        }

        Match prefix = LinePrefix().Match(line);
        if (!prefix.Success)
        {
            return null;
        }

        if (!DateTime.TryParseExact(prefix.Groups["ts"].Value, TimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime timestamp))
        {
            return null;
        }

        LogCategory category = ParseCategory(prefix.Groups["cat"].Value);
        string body = StripTags(prefix.Groups["body"].Value);

        return category switch
        {
            LogCategory.Combat => _ParseCombat(grammar, timestamp, body, prefix.Groups["body"].Value),
            LogCategory.Mining => _ParseMining(grammar, timestamp, body),
            LogCategory.None => _ParseLocation(grammar, timestamp, body),
            LogCategory.Bounty => _ParseBounty(grammar, timestamp, body),
            LogCategory.Notify or LogCategory.Warning => _ParseNotify(timestamp, body, language),
            _ => null
        };
    }

    private static GameLogEvent? _ParseLocation(GamelogGrammar grammar, DateTime timestamp, string body)
    {
        Match jump = grammar.Jumping.Match(body);
        if (jump.Success)
        {
            return new LocationEvent(timestamp, jump.Groups["system"].Value.Trim());
        }

        Match undock = grammar.Undocking.Match(body);
        return undock.Success ? new LocationEvent(timestamp, undock.Groups["system"].Value.Trim()) : null;
    }

    private static GameLogEvent? _ParseBounty(GamelogGrammar grammar, DateTime timestamp, string body)
    {
        Match match = grammar.Bounty.Match(body);
        if (!match.Success)
        {
            return null;
        }

        return long.TryParse(_Digits(match.Groups["isk"].Value), NumberStyles.Integer, CultureInfo.InvariantCulture, out long isk)
            ? new BountyEvent(timestamp, isk)
            : null;
    }

    private static GameLogEvent? _ParseNotify(DateTime timestamp, string body, GamelogLanguage language) =>
        string.IsNullOrWhiteSpace(body) ? null : new NotifyEvent(timestamp, body, language);

    private static GameLogEvent? _ParseCombat(GamelogGrammar grammar, DateTime timestamp, string body, string rawBody)
    {
        if (_ParseMiss(grammar, timestamp, body) is { } miss)
        {
            return miss;
        }

        if (_FirstMatch(grammar.Repairs, body) is { } repair && _ParseAmount(repair.Match.Groups["amount"].Value) is { } repAmount)
        {
            return new RemoteRepEvent(timestamp, repair.Pattern.Direction == LineDirection.Outgoing, repAmount,
                repair.Pattern.Kind ?? string.Empty, CounterpartyOf(repair.Match.Groups["rest"].Value.Trim()));
        }

        if (_FirstMatch(grammar.CapTransfers, body) is { } cap && _ParseAmount(cap.Match.Groups["amount"].Value) is { } capAmount)
        {
            return new CapTransferEvent(timestamp, cap.Pattern.Direction == LineDirection.Outgoing, capAmount,
                cap.Match.Groups["rest"].Value.Trim());
        }

        if (_FirstMatch(grammar.Neutralizers, body) is { } neut && _ParseAmount(neut.Match.Groups["amount"].Value) is { } neutAmount)
        {
            // Where the words don't tell the direction, the lead colour does (incoming = 0xffe57f7f, outgoing =
            // 0xff7fffff).
            bool outgoing = neut.Pattern.Direction == LineDirection.ByColour
                ? !string.Equals(_LeadColor(rawBody), IncomingNeutColor, StringComparison.OrdinalIgnoreCase)
                : neut.Pattern.Direction == LineDirection.Outgoing;
            return new NeutEvent(timestamp, outgoing, neutAmount, neut.Match.Groups["rest"].Value.Trim());
        }

        return _ParseDamage(grammar, timestamp, body, rawBody);
    }

    // "<amount> to|from <target> [- <weapon>] - <quality>"
    private static CombatEvent? _ParseDamage(GamelogGrammar grammar, DateTime timestamp, string body, string rawBody)
    {
        string[] segments = body.Split(ModuleSeparator);
        if (segments.Length < 2)
        {
            return null;
        }

        Match head = grammar.DamageHead.Match(segments[0]);
        if (!head.Success || _ParseAmount(head.Groups["amount"].Value) is not { } amount)
        {
            return null;
        }

        if (!grammar.Qualities.TryGetValue(segments[^1], out HitQuality quality))
        {
            return null;
        }

        DamageDirection? direction = grammar.DirectionByColour
            ? _DirectionFromColor(_LeadColor(rawBody))
            : head.Groups["to"].Success ? DamageDirection.Outgoing : DamageDirection.Incoming;
        if (direction is null)
        {
            return null;
        }

        string? weapon = segments.Length >= 3 ? segments[^2] : null;
        return new CombatEvent(timestamp, direction.Value, amount, head.Groups["target"].Value, weapon, quality);
    }

    private static CombatEvent? _ParseMiss(GamelogGrammar grammar, DateTime timestamp, string body)
    {
        // Outgoing: "Your <weapon> misses <target> completely - <weapon>"
        // Incoming: "<source> misses you completely"
        foreach (Regex regex in grammar.OutgoingMisses)
        {
            Match match = regex.Match(body);
            if (match.Success)
            {
                string? weapon = match.Groups["tail"].Success ? _LastSegment(match.Groups["tail"].Value) : null;
                return new CombatEvent(timestamp, DamageDirection.Outgoing, 0, match.Groups["target"].Value.Trim(), weapon, HitQuality.Misses);
            }
        }

        foreach (Regex regex in grammar.IncomingMisses)
        {
            Match match = regex.Match(body);
            if (match.Success)
            {
                Group who = match.Groups["source"].Success ? match.Groups["source"] : match.Groups["owner"];
                string source = (who.Value + match.Groups["tail"].Value).Trim();
                return new CombatEvent(timestamp, DamageDirection.Incoming, 0, source, null, HitQuality.Misses);
            }
        }

        return null;
    }

    private static GameLogEvent? _ParseMining(GamelogGrammar grammar, DateTime timestamp, string body)
    {
        Match critical = grammar.CriticalMined.Match(body);
        if (critical.Success)
        {
            return _ParseAmount(critical.Groups["amount"].Value) is { } units
                ? new MiningEvent(timestamp, units, critical.Groups["ore"].Value.Trim(), IsCritical: true, LostResidue: 0)
                : null;
        }

        Match mined = grammar.Mined.Match(body);
        if (mined.Success)
        {
            return _ParseAmount(mined.Groups["amount"].Value) is { } units
                ? new MiningEvent(timestamp, units, mined.Groups["ore"].Value.Trim(), IsCritical: false, LostResidue: 0)
                : null;
        }

        Match residue = grammar.MiningResidue.Match(body);
        return residue.Success && _ParseAmount(residue.Groups["amount"].Value) is { } lost
            ? new MiningResidueEvent(timestamp, lost)
            : null;
    }

    private static PatternMatch? _FirstMatch(IReadOnlyList<DirectedPattern> patterns, string body)
    {
        foreach (DirectedPattern pattern in patterns)
        {
            Match match = pattern.Regex.Match(body);
            if (match.Success)
            {
                return new PatternMatch(pattern, match);
            }
        }

        return null;
    }

    private sealed record PatternMatch(DirectedPattern Pattern, Match Match);

    private static string _LeadColor(string rawBody)
    {
        Match lead = LeadColor().Match(rawBody);
        return lead.Success ? lead.Groups["hex"].Value : string.Empty;
    }

    private static DamageDirection? _DirectionFromColor(string hex) => hex.ToLowerInvariant() switch
    {
        OutgoingDamageColor => DamageDirection.Outgoing,
        IncomingDamageColor => DamageDirection.Incoming,
        _ => null
    };

    private static string _LastSegment(string tail) => tail.Split(ModuleSeparator)[^1].Trim();

    private static int? _ParseAmount(string text) =>
        int.TryParse(_Digits(text), NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) ? amount : null;

    private static string _Digits(string text) => new([.. text.Where(char.IsAsciiDigit)]);
}
