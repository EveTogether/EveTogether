using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>
/// Turns the raw "Effect Beacon" (group 920) type names into the curated environment set the fit-simulator picker shows
/// . Group 920 also holds dozens of non-environment beacons (faction-war HQs, capsuleer-day and
/// SOEEB event beacons, tournament beacons) — those carry no fit-relevant effect or no stable name, so the classifier
/// keeps only the wormhole class effects, the metaliminal storms and the Triglavian-invasion system effects, all of
/// which carry real, data-driven category-7 modifiers in the SDE. Shared by every <see cref="ISdeAccessor"/> so the
/// name parsing has a single source of truth (the live store and the test fake classify identically).
/// </summary>
public static class EnvironmentBeaconClassifier
{
    public const string Wormhole = "Wormhole";
    public const string Metaliminal = "Metaliminal Storm";
    public const string Triglavian = "Triglavian";

    private const int WormholeBase = 1000;
    private const int MetaliminalBase = 2000;
    private const int TriglavianBase = 3000;

    // "Class 1 Pulsar Effects" — the six wormhole phenomena across classes 1..6 (the SDE spells "Wolf Rayet" un-hyphenated).
    private static readonly Regex WormholeName =
        new(@"^Class (?<tier>[1-6]) (?<family>Pulsar|Black Hole|Cataclysmic Variable|Magnetar|Red Giant|Wolf Rayet) Effects$",
            RegexOptions.Compiled);

    // "Strong Metaliminal Electrical Storm" — the four k-space metaliminal storms in Weak/Strong strengths.
    private static readonly Regex MetaliminalName =
        new(@"^(?<strength>Weak|Strong) Metaliminal (?<family>Electrical|Exotic Matter|Gamma Ray|Plasma Firestorm) Storm$",
            RegexOptions.Compiled);

    // "Triglavian Invasion Strong System Effects" — the three invasion strengths.
    private static readonly Regex TriglavianName =
        new(@"^Triglavian Invasion (?<strength>Mild|Moderate|Strong) System Effects$", RegexOptions.Compiled);

    private static readonly string[] WormholeFamilies =
        ["Pulsar", "Black Hole", "Cataclysmic Variable", "Magnetar", "Red Giant", "Wolf Rayet"];
    private static readonly string[] MetaliminalFamilies =
        ["Electrical", "Exotic Matter", "Gamma Ray", "Plasma Firestorm"];
    private static readonly string[] TriglavianStrengths = ["Mild", "Moderate", "Strong"];

    /// <summary>Classifies a group-920 type into a curated environment beacon, or null when it is not one of the
    /// supported phenomena.</summary>
    public static SdeEnvironmentBeacon? Classify(int typeId, string name)
    {
        if (WormholeName.Match(name) is { Success: true } wh)
        {
            var tier = int.Parse(wh.Groups["tier"].Value);
            var family = wh.Groups["family"].Value;
            var display = family == "Wolf Rayet" ? "Wolf-Rayet" : family;
            var order = WormholeBase + System.Array.IndexOf(WormholeFamilies, family) * 10 + tier;
            return new SdeEnvironmentBeacon(typeId, $"{display} C{tier}", Wormhole, order);
        }

        if (MetaliminalName.Match(name) is { Success: true } meta)
        {
            var strength = meta.Groups["strength"].Value;
            var family = meta.Groups["family"].Value;
            var rank = strength == "Strong" ? 2 : 1;
            var order = MetaliminalBase + System.Array.IndexOf(MetaliminalFamilies, family) * 10 + rank;
            return new SdeEnvironmentBeacon(typeId, $"{family} Storm ({strength})", Metaliminal, order);
        }

        if (TriglavianName.Match(name) is { Success: true } trig)
        {
            var strength = trig.Groups["strength"].Value;
            var order = TriglavianBase + System.Array.IndexOf(TriglavianStrengths, strength) + 1;
            return new SdeEnvironmentBeacon(typeId, $"Triglavian Invasion ({strength})", Triglavian, order);
        }

        return null;
    }
}
