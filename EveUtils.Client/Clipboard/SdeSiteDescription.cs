using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// How a copied site name is described from the catalogue. One implementation for both readers — the toast that
/// offers the run and the window that runs it must not answer differently about the same site (ET-79 AC-5).
/// Never fabricates a field the SDE does not carry, and never picks one match over another.
/// </summary>
internal static class SdeSiteDescription
{
    /// <summary>What the matches agree on and nothing else — empty when there is nothing to say. This is the
    /// window's form: it describes the site, never the catalogue's shape.</summary>
    public static string DescribeCommon(IReadOnlyList<SdeSite> matches) => matches.Count switch
    {
        0 => string.Empty,
        1 => DescribeOne(matches[0]),
        _ => DescribeShared(matches)
    };

    /// <summary>The toast's form: the same shared facts, plus how many variants are behind them.</summary>
    public static string DescribeMatches(IReadOnlyList<SdeSite> matches)
    {
        var distinctDescriptions = matches.Select(DescribeOne).Distinct().ToList();
        if (distinctDescriptions.Count == 1)
            return distinctDescriptions[0];

        var shared = DescribeShared(matches);
        var variants = $"{distinctDescriptions.Count} variants";
        return shared.Length == 0 ? variants : $"{shared} · {variants}";
    }

    public static string DescribeOne(SdeSite site)
    {
        var facts = new List<string>();
        if (site.ArchetypeName is not null)
            facts.Add(site.ArchetypeName);
        if (IsKnownFaction(site.FactionName))
            facts.Add(site.FactionName!);
        if (site.DedRating is { } ded)
            facts.Add($"DED {ded}");
        if (site.IsShipRestricted)
            facts.Add("ship-restricted");

        return string.Join(" · ", facts);
    }

    // Shares only what every remaining match agrees on; anything the matches disagree on is left out rather than
    // guessed at (ET-79 AC-5).
    public static string DescribeShared(IReadOnlyList<SdeSite> matches)
    {
        var facts = new List<string>();
        if (matches.Select(s => s.ArchetypeName).Distinct().ToList() is [{ } archetype])
            facts.Add(archetype);
        if (matches.Select(s => IsKnownFaction(s.FactionName) ? s.FactionName : null).Distinct().ToList() is [{ } faction])
            facts.Add(faction);
        if (matches.Select(s => s.DedRating).Distinct().ToList() is [{ } ded])
            facts.Add($"DED {ded}");
        if (matches.Select(s => s.IsShipRestricted).Distinct().ToList() is [true])
            facts.Add("ship-restricted");

        return string.Join(" · ", facts);
    }

    private static bool IsKnownFaction(string? factionName) => factionName is not (null or "Unknown");
}
