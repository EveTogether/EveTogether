using System.Globalization;

namespace EveUtils.Shared.Modules.Killmails;

/// <summary>One attacker line group from the "Involved parties:" block.</summary>
public sealed record KillmailTextAttacker(string Name, bool FinalBlow, string? CorporationName, string? AllianceName,
    string? FactionName, string? ShipName, string? WeaponName, int? DamageDone);

/// <summary>One destroyed or dropped item line, aggregated by name and cargo flag — a duplicate line (the same
/// name, same cargo flag) is summed, never deduplicated (ET-340).</summary>
public sealed record KillmailTextItem(string Name, long Quantity, bool IsCargo);

/// <summary>
/// A killmail captured from the "Copy" button's clipboard text, before any SDE resolution (ET-340) —
/// <see cref="VictimShipName"/>, item names and <see cref="SystemName"/> are the raw in-game names, resolved to SDE
/// ids by the caller.
/// </summary>
public sealed record KillmailTextCapture(
    DateTime KillmailTimeUtc,
    string VictimName,
    string? VictimCorporationName,
    string? VictimAllianceName,
    string? VictimFactionName,
    string VictimShipName,
    string? SystemName,
    int? DamageTaken,
    IReadOnlyList<KillmailTextAttacker> Attackers,
    IReadOnlyList<KillmailTextItem> DestroyedItems,
    IReadOnlyList<KillmailTextItem> DroppedItems);

/// <summary>
/// Parses the in-game killmail clipboard text (ET-340) — pure text work, no SDE and no ESI. The text carries no
/// killmail id or hash, unlike a pasted link (<see cref="KillmailLink"/>): matching a later real mail against a
/// row parsed here happens on time + victim + ship instead (<c>IProvisionalKillmailRepository.RemoveMatchingAsync</c>).
/// </summary>
public static class KillmailTextParser
{
    private const string TimestampFormat = "yyyy.MM.dd HH:mm:ss";
    private const string FinalBlowSuffix = " (laid the final blow)";
    private const string QtySeparator = ", Qty: ";
    private const string CargoSuffix = " (Cargo)";

    /// <summary>Null when <paramref name="text"/> carries no recognisable victim line — never throws.</summary>
    public static KillmailTextCapture? Parse(string text)
    {
        DateTime? killmailTimeUtc = null;
        string? victimName = null, victimCorp = null, victimAlliance = null, victimFaction = null, victimShip = null;
        string? systemName = null;
        int? damageTaken = null;
        List<KillmailTextAttacker> attackers = [];
        List<KillmailTextItem> destroyed = [];
        List<KillmailTextItem> dropped = [];
        var section = _Section.Header;

        string? attackerName = null;
        bool attackerFinalBlow = false;
        string? attackerCorp = null, attackerAlliance = null, attackerFaction = null, attackerShip = null, attackerWeapon = null;

        foreach (var rawLine in text.Split('\n'))
        {
            string line = (rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (killmailTimeUtc is null
                && DateTime.TryParseExact(line, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
            {
                killmailTimeUtc = DateTime.SpecifyKind(parsedTime, DateTimeKind.Utc);
                continue;
            }

            if (line == "Involved parties:")
            {
                section = _Section.Involved;
                continue;
            }

            if (line == "Destroyed items:")
            {
                section = _Section.Destroyed;
                continue;
            }

            if (line == "Dropped items:")
            {
                section = _Section.Dropped;
                continue;
            }

            switch (section)
            {
                case _Section.Header:
                    (string Label, string Value)? victimField = _SplitLabel(line);
                    if (victimField is { } field)
                    {
                        switch (field.Label)
                        {
                            case "Victim": victimName = field.Value; break;
                            case "Corp": victimCorp = _NullIfNoneOrUnknown(field.Value); break;
                            case "Alliance": victimAlliance = _NullIfNoneOrUnknown(field.Value); break;
                            case "Faction": victimFaction = _NullIfNoneOrUnknown(field.Value); break;
                            case "Destroyed": victimShip = field.Value; break;
                            case "System": systemName = field.Value; break;
                            case "Damage Taken":
                                damageTaken = int.TryParse(field.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var taken) ? taken : null;
                                break;
                        }
                    }
                    break;

                case _Section.Involved:
                    (string Label, string Value)? involvedField = _SplitLabel(line);
                    if (involvedField is { } attackerLine)
                    {
                        switch (attackerLine.Label)
                        {
                            case "Name":
                                attackerFinalBlow = attackerLine.Value.EndsWith(FinalBlowSuffix, StringComparison.Ordinal);
                                attackerName = attackerFinalBlow ? attackerLine.Value[..^FinalBlowSuffix.Length] : attackerLine.Value;
                                break;
                            case "Corp": attackerCorp = _NullIfNoneOrUnknown(attackerLine.Value); break;
                            case "Alliance": attackerAlliance = _NullIfNoneOrUnknown(attackerLine.Value); break;
                            case "Faction": attackerFaction = _NullIfNoneOrUnknown(attackerLine.Value); break;
                            case "Ship": attackerShip = _NullIfNoneOrUnknown(attackerLine.Value); break;
                            case "Weapon": attackerWeapon = _NullIfNoneOrUnknown(attackerLine.Value); break;
                            case "Damage Done":
                                if (attackerName is not null)
                                {
                                    int? damageDone = int.TryParse(attackerLine.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var done) ? done : null;
                                    attackers.Add(new KillmailTextAttacker(attackerName, attackerFinalBlow, attackerCorp, attackerAlliance,
                                        attackerFaction, attackerShip, attackerWeapon, damageDone));
                                }
                                attackerName = null;
                                attackerFinalBlow = false;
                                attackerCorp = attackerAlliance = attackerFaction = attackerShip = attackerWeapon = null;
                                break;
                        }
                    }
                    break;

                case _Section.Destroyed:
                    destroyed.Add(_ParseItem(line));
                    break;

                case _Section.Dropped:
                    dropped.Add(_ParseItem(line));
                    break;
            }
        }

        return killmailTimeUtc is null || victimName is null || victimShip is null
            ? null
            : new KillmailTextCapture(killmailTimeUtc.Value, victimName, victimCorp, victimAlliance, victimFaction, victimShip,
                systemName, damageTaken, attackers, _Aggregate(destroyed), _Aggregate(dropped));
    }

    // "Label: value" — the Involved/Victim blocks share this shape for every field; ": " (not just ":") avoids
    // splitting a value that itself contains a colon.
    private static (string Label, string Value)? _SplitLabel(string line)
    {
        int separator = line.IndexOf(": ", StringComparison.Ordinal);
        return separator < 0 ? null : (line[..separator], line[(separator + 2)..].Trim());
    }

    // "None" (no alliance/faction) and "Unknown" (faction not shown) both mean "nothing here" (ET-340).
    private static string? _NullIfNoneOrUnknown(string value) => value is "None" or "Unknown" ? null : value;

    // "Null S, Qty: 78 " -> ("Null S", 78, false); "Light Neutron Blaster II" (no Qty) -> quantity 1.
    private static KillmailTextItem _ParseItem(string line)
    {
        bool isCargo = line.EndsWith(CargoSuffix, StringComparison.Ordinal);
        string body = (isCargo ? line[..^CargoSuffix.Length] : line).TrimEnd();

        int qtyIndex = body.IndexOf(QtySeparator, StringComparison.Ordinal);
        if (qtyIndex < 0)
        {
            return new KillmailTextItem(body.Trim(), 1, isCargo);
        }

        string name = body[..qtyIndex].Trim();
        string qtyText = body[(qtyIndex + QtySeparator.Length)..].Trim();
        long quantity = long.TryParse(qtyText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
        return new KillmailTextItem(name, quantity, isCargo);
    }

    // A duplicate line (the same charge stacked per weapon, e.g. two "Null S, Qty: 78" lines) is summed, never
    // deduplicated — the cargo flag keeps a cargo stack and a fitted/dropped stack of the same item apart (ET-340).
    private static IReadOnlyList<KillmailTextItem> _Aggregate(List<KillmailTextItem> items) =>
        [.. items.GroupBy(item => (item.Name, item.IsCargo))
            .Select(group => new KillmailTextItem(group.Key.Name, group.Sum(item => item.Quantity), group.Key.IsCargo))];

    private enum _Section { Header, Involved, Destroyed, Dropped }
}
