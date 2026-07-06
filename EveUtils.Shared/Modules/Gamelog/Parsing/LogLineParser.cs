using System.Globalization;
using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Parsing;

/// <summary>Parses EVE gamelog lines into <see cref="GameLogEvent"/>s. Folded from the EVE-Utils demo (own code).</summary>
public static partial class LogLineParser
{
    private const string TimestampFormat = "yyyy.MM.dd HH:mm:ss";

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^\[ (?<ts>\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2}) \] \((?<cat>\w+)\) (?<body>.*)$")]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"^(?<amount>\d+) (?<dir>to|from) (?<target>.+)$")]
    private static partial Regex DamageHead();

    // Only true reps (armor/shield/hull) — NOT "remote capacitor transmitted", which is cap warfare, not a heal, and
    // must never count toward repaired HP. Cap transfer has its own event (CapTransfer below).
    [GeneratedRegex(@"^(?<amt>\d+) remote (?<kind>armor|shield|hull) (?:repaired|boosted) (?<dir>to|by) (?<rest>.+)$")]
    private static partial Regex RemoteRep();

    // Remote capacitor transmitted — cap support. Direction from to/by (you transmit "to" / cap transmitted "by" someone).
    [GeneratedRegex(@"^(?<amt>\d+) remote capacitor transmitted (?<dir>to|by) (?<rest>.+)$")]
    private static partial Regex CapTransfer();

    // Energy neutralizer (no to/from in the text — direction comes from the line's lead colour). After StripTags:
    // "{amt} GJ energy neutralized {target} - {module}". Distinct from "energy drained" (nosferatu), which we don't parse.
    [GeneratedRegex(@"^(?<amt>\d+) GJ energy neutralized (?<rest>.+)$")]
    private static partial Regex EnergyNeut();

    // The line's leading colour tag (the amount's colour) — EVE encodes energy-warfare direction here, not in the text.
    [GeneratedRegex(@"^<color=0x(?<hex>[0-9a-fA-F]{8})>")]
    private static partial Regex LeadColor();

    // Neut direction by lead colour, validated against real gamelogs + the cono reference (erstschlag/cono, reference
    // only — own implementation): incoming neut (applied to you) = 0xffe57f7f, outgoing neut (you neuting a target) =
    // 0xff7fffff. Any other colour on an "energy neutralized" line is treated as outgoing (only these two are emitted).
    private const string IncomingNeutColor = "ffe57f7f";

    [GeneratedRegex(@"^You mined (?<units>\d+) units of (?<ore>.+?)(?: with a lost residue of (?<residue>\d+) units)?$")]
    private static partial Regex Mined();

    [GeneratedRegex(@"^Critical mining success! You mined an additional (?<units>\d+) units of (?<ore>.+)$")]
    private static partial Regex CriticalMined();

    [GeneratedRegex(@"^Jumping from .+? to (?<sys>.+)$")]
    private static partial Regex Jumping();

    [GeneratedRegex(@"^Undocking from .+ to (?<sys>.+?) solar system\.?$")]
    private static partial Regex Undocking();

    [GeneratedRegex(@"^(?<isk>[\d,]+) ISK added to next bounty payout$")]
    private static partial Regex Bounty();

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

    public static GameLogEvent? Parse(string line)
    {
        var prefix = LinePrefix().Match(line);
        if (!prefix.Success)
            return null;

        if (!DateTime.TryParseExact(prefix.Groups["ts"].Value, TimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            return null;

        var category = ParseCategory(prefix.Groups["cat"].Value);
        var body = StripTags(prefix.Groups["body"].Value);

        return category switch
        {
            LogCategory.Combat => ParseCombat(timestamp, body, prefix.Groups["body"].Value),
            LogCategory.Mining => ParseMining(timestamp, body),
            LogCategory.None => ParseLocation(timestamp, body),
            LogCategory.Bounty => ParseBounty(timestamp, body),
            LogCategory.Notify or LogCategory.Warning => ParseNotify(timestamp, body),
            _ => null
        };
    }

    private static GameLogEvent? ParseLocation(DateTime timestamp, string body)
    {
        var jump = Jumping().Match(body);
        if (jump.Success)
            return new LocationEvent(timestamp, jump.Groups["sys"].Value.Trim());

        var undock = Undocking().Match(body);
        if (undock.Success)
            return new LocationEvent(timestamp, undock.Groups["sys"].Value.Trim());

        return null;
    }

    private static GameLogEvent? ParseBounty(DateTime timestamp, string body)
    {
        var m = Bounty().Match(body);
        if (!m.Success)
            return null;

        var raw = m.Groups["isk"].Value.Replace(",", string.Empty);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var isk)
            ? new BountyEvent(timestamp, isk)
            : null;
    }

    private static GameLogEvent? ParseNotify(DateTime timestamp, string body) =>
        string.IsNullOrWhiteSpace(body) ? null : new NotifyEvent(timestamp, body);

    private static GameLogEvent? ParseCombat(DateTime timestamp, string body, string rawBody)
    {
        if (body.Contains(" misses ", StringComparison.Ordinal))
            return ParseMiss(timestamp, body);

        var rep = RemoteRep().Match(body);
        if (rep.Success)
        {
            var counterparty = rep.Groups["rest"].Value.Split(" - ")[0].Trim();
            return new RemoteRepEvent(
                timestamp,
                rep.Groups["dir"].Value == "to",
                int.Parse(rep.Groups["amt"].Value, CultureInfo.InvariantCulture),
                rep.Groups["kind"].Value,
                counterparty);
        }

        var cap = CapTransfer().Match(body);
        if (cap.Success)
        {
            return new CapTransferEvent(
                timestamp,
                cap.Groups["dir"].Value == "to",
                int.Parse(cap.Groups["amt"].Value, CultureInfo.InvariantCulture));
        }

        var neut = EnergyNeut().Match(body);
        if (neut.Success)
        {
            // No to/from token — direction is the lead colour (incoming = 0xffe57f7f, outgoing = 0xff7fffff).
            var lead = LeadColor().Match(rawBody);
            var incoming = lead.Success && string.Equals(lead.Groups["hex"].Value, IncomingNeutColor, StringComparison.OrdinalIgnoreCase);
            return new NeutEvent(
                timestamp,
                Outgoing: !incoming,
                int.Parse(neut.Groups["amt"].Value, CultureInfo.InvariantCulture));
        }

        // "<amount> to|from <target> [- <weapon>] - <quality>"
        var segments = body.Split(" - ");
        if (segments.Length < 2)
            return null;

        var head = DamageHead().Match(segments[0]);
        if (!head.Success)
            return null;

        if (!TryParseQuality(segments[^1], out var quality))
            return null;

        var weapon = segments.Length >= 3 ? segments[^2] : null;
        var direction = head.Groups["dir"].Value == "to" ? DamageDirection.Outgoing : DamageDirection.Incoming;

        return new CombatEvent(
            timestamp,
            direction,
            int.Parse(head.Groups["amount"].Value, CultureInfo.InvariantCulture),
            head.Groups["target"].Value,
            weapon,
            quality);
    }

    private static CombatEvent ParseMiss(DateTime timestamp, string body)
    {
        // Outgoing: "Your <weapon> misses <target> completely - <weapon>"
        // Incoming: "<target> misses you completely"
        if (body.StartsWith("Your ", StringComparison.Ordinal))
        {
            var weapon = body.Split(" - ").Length >= 2 ? body.Split(" - ")[^1] : null;
            var target = ExtractMissTarget(body, "misses ", " completely");
            return new CombatEvent(timestamp, DamageDirection.Outgoing, 0, target, weapon, HitQuality.Misses);
        }

        var source = body.Replace(" misses you completely", string.Empty, StringComparison.Ordinal).Trim();
        return new CombatEvent(timestamp, DamageDirection.Incoming, 0, source, null, HitQuality.Misses);
    }

    private static string ExtractMissTarget(string body, string after, string before)
    {
        var start = body.IndexOf(after, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;
        start += after.Length;
        var end = body.IndexOf(before, start, StringComparison.Ordinal);
        return end < 0 ? body[start..].Trim() : body[start..end].Trim();
    }

    private static GameLogEvent? ParseMining(DateTime timestamp, string body)
    {
        var critical = CriticalMined().Match(body);
        if (critical.Success)
        {
            return new MiningEvent(
                timestamp,
                int.Parse(critical.Groups["units"].Value, CultureInfo.InvariantCulture),
                critical.Groups["ore"].Value.Trim(),
                IsCritical: true,
                LostResidue: 0);
        }

        var mined = Mined().Match(body);
        if (!mined.Success)
            return null;

        var residue = mined.Groups["residue"].Success
            ? int.Parse(mined.Groups["residue"].Value, CultureInfo.InvariantCulture)
            : 0;

        return new MiningEvent(
            timestamp,
            int.Parse(mined.Groups["units"].Value, CultureInfo.InvariantCulture),
            mined.Groups["ore"].Value.Trim(),
            IsCritical: false,
            residue);
    }

    private static bool TryParseQuality(string text, out HitQuality quality)
    {
        quality = text switch
        {
            "Hits" => HitQuality.Hits,
            "Penetrates" => HitQuality.Penetrates,
            "Grazes" => HitQuality.Grazes,
            "Smashes" => HitQuality.Smashes,
            "Glances Off" => HitQuality.Glances,
            "Wrecks" => HitQuality.Wrecks,
            _ => HitQuality.Misses
        };
        return text is "Hits" or "Penetrates" or "Grazes" or "Smashes" or "Glances Off" or "Wrecks";
    }
}
