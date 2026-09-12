using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// What CONSUMABLES needs about the abyssal filament itself (ET-249): its SDE type, resolved from the pocket's own
/// stored tier and weather, and how many a hull class is known to need. Data, not a formula — a hull class this has
/// no rule for proposes nothing rather than a guess, and the caller is expected to leave the field for the pilot.
/// </summary>
public static class AbyssalConsumables
{
    /// <summary>Filaments needed per hull class, keyed by the SDE's own ship-group name. Source: Jithran, ET-249
    /// (2026-09-11) for the destroyer, and ET-263 (2026-09-12) for the frigate and cruiser — the SDE does not carry
    /// this either, checked at ET-249. A hull class not in this table proposes no count at all.</summary>
    private static readonly IReadOnlyDictionary<string, int> CountByHullClass = new Dictionary<string, int>
    {
        ["Frigate"] = 3,
        ["Destroyer"] = 2,
        ["Cruiser"] = 1
    };

    /// <summary>The filament count a hull class is known to need, or null when nobody has measured it yet.</summary>
    public static int? ProposedCount(string? hullClass) =>
        hullClass is not null && CountByHullClass.TryGetValue(hullClass, out int count) ? count : null;

    /// <summary>The filament item the pocket's own tier and weather names (e.g. "Agitated Dark Filament"), resolved
    /// to its SDE type id by exact name. Group 1979 holds exactly 35 published types, one per tier-weather
    /// combination (7 tiers x 5 weathers, ET-172), each named "{tier} {weather} Filament" — so the exact name is
    /// enough and no group lookup is needed. Null while the SDE has no type by that name, or is unavailable —
    /// never a guessed id.</summary>
    public static int? ResolveTypeId(ISdeAccessor sde, int tierIndex, string weatherName) =>
        sde.IsAvailable && tierIndex >= 0 && tierIndex < AbyssalTiers.Names.Count
        && sde.TryGetTypeId($"{AbyssalTiers.Names[tierIndex]} {weatherName} Filament", out int typeId)
            ? typeId
            : null;
}
