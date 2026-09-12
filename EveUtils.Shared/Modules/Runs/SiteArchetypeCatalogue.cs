using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Runs;

/// <summary>
/// ET-275: CCP's own site archetypes (the SDE's <c>Site.archetypeId</c>), collapsed into the one
/// <see cref="RunTypeId"/> a whole family of them shares. Measured against the installed SDE build (24 "Combat
/// Sites", 27 "Ore Anomalies", 31 "Escalation", 33-36 the four DED-complex archetypes) rather than hardcoded ids of
/// unknown provenance — the mapping itself is fixed CCP taxonomy, but which dungeon carries which name is not.
///
/// Homefront (70) is deliberately absent: <see cref="HomefrontCatalogue"/> already resolves it by dungeon id, with
/// its own per-kind refinement (Raid, Metaliminal Meteoroid, …) this table has no room for, and every one of its 24
/// names is unique in the catalogue (domain/homefronts.md §2) — it never needs the name-level fallback below.
/// </summary>
public static class SiteArchetypeCatalogue
{
    private static readonly IReadOnlyDictionary<int, RunTypeId> RunTypeByArchetypeId = new Dictionary<int, RunTypeId>
    {
        [24] = RunTypeId.CombatSite,
        [31] = RunTypeId.CombatSite,
        [33] = RunTypeId.CombatSite,
        [34] = RunTypeId.CombatSite,
        [35] = RunTypeId.CombatSite,
        [36] = RunTypeId.CombatSite,
        [27] = RunTypeId.OreSite
    };

    /// <summary>
    /// The type every SDE dungeon carrying <paramref name="siteName"/> resolves to, when every one of them maps to
    /// the same <see cref="RunTypeId"/> above — without ever picking which of them this run actually was
    /// (ET-275 AC-1). Null when the name is absent from the installed SDE, carries a dungeon whose archetype this
    /// table does not know, or spans more than one resulting type: "Sansha Refuge" (two dungeons, both archetype 24)
    /// resolves to <see cref="RunTypeId.CombatSite"/> this way; a name mixing a mapped and an unmapped archetype, or
    /// two mapped archetypes that land on different types, resolves to null — nothing here ever guesses (ET-275
    /// AC-3, "Site" stays "Site").
    /// </summary>
    public static RunTypeId? ResolveByName(ISdeAccessor sde, string siteName)
    {
        IReadOnlyList<SdeSite> sites = sde.FindSitesByExactName(siteName);
        if (sites.Count == 0)
            return null;

        RunTypeId? resolved = null;
        foreach (SdeSite site in sites)
        {
            if (site.ArchetypeId is not { } archetypeId || !RunTypeByArchetypeId.TryGetValue(archetypeId, out RunTypeId candidate))
                return null;
            if (resolved is { } already && already != candidate)
                return null;

            resolved = candidate;
        }

        return resolved;
    }
}
