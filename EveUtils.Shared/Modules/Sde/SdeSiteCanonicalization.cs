using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde;

/// <summary>
/// Collapses catalogue "twins" to one canonical row per group, in the catalogue layer so every reader — the manual
/// run-start picker, the escalation dialog, and whatever reads the catalogue next — resolves the same twin to the
/// same id, rather than each rebuilding its own answer (Raymond, 2026-09-05).
///
/// Equality is a SHA-256 hash over the catalogue fields (the fittings' own <c>ContentHash</c> pattern — this is not
/// a new idea, just its second application): more precise than a hand-rolled field comparison and, because the
/// rule lives in one function, unable to drift between readers. Two rows are a twin only when their hash matches —
/// agreement on every field the catalogue carries: name, archetype, faction, DED, ship restriction, description. A
/// pair that disagrees on any of those (Sansha's Command Relay Outpost: <c>2251</c> a Combat Site, <c>2406</c> an
/// Escalation) hashes differently and stays two rows, each keeping the field that told them apart.
/// </summary>
public static class SdeSiteCanonicalization
{
    /// <summary>
    /// One row per twin group, canonicalised to that group's lowest <c>dungeonId</c> — not the hash itself. A run's
    /// <c>SiteTypeId</c> IS the dungeonId today (StartRunCommandHandler), with <c>SiteTypeSource</c> saying which id
    /// space it came from; writing the hash there instead would be a new id space and a break with every run already
    /// stored. Grouping by hash and keeping dungeonId as the stored value takes the good side of both: the equality
    /// rule stays reproducible even if a later SDE build changes a field (the hash moves, the dungeonId does not),
    /// and a third twin joining a pair (which could shift "the lowest id") cannot change which hash a run belongs to.
    /// "Lowest id" is deterministic and independent of input order: every client importing the same SDE build hashes
    /// the same rows into the same groups and takes the same minimum, so nobody has to coordinate a choice.
    /// </summary>
    public static IReadOnlyList<SdeSite> Canonicalize(IReadOnlyList<SdeSite> sites) =>
        [.. sites
            .GroupBy(EqualityKey, StringComparer.Ordinal)
            .Select(group => group.OrderBy(site => site.DungeonId).First())
            .OrderBy(site => site.Name, StringComparer.Ordinal)
            .ThenBy(site => site.DungeonId)];

    /// <summary>
    /// SHA-256 over name, archetype, faction, DED, ship restriction and description, in that fixed order — written
    /// down here rather than left to a record's own member order, which is an implementation detail free to change.
    /// The ASCII Unit Separator (0x1F, a control character never present in SDE text) joins the fields so
    /// <c>"ab"+"c"</c> and <c>"a"+"bc"</c> can never hash the same. Every field is upper-invariant text or
    /// invariant-culture formatted, so a comma-decimal client and a dot-decimal client hash the same site the same.
    /// </summary>
    private static string EqualityKey(SdeSite site)
    {
        const char fieldSeparator = (char)0x1F; // ASCII Unit Separator
        string reproducible = string.Join(fieldSeparator,
            site.Name.ToUpperInvariant(),
            site.ArchetypeName?.ToUpperInvariant() ?? string.Empty,
            site.FactionName?.ToUpperInvariant() ?? string.Empty,
            site.DedRating?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            site.IsShipRestricted.ToString(CultureInfo.InvariantCulture),
            site.Description?.ToUpperInvariant() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reproducible)));
    }
}
