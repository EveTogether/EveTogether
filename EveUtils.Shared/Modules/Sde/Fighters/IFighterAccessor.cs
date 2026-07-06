namespace EveUtils.Shared.Modules.Sde.Fighters;

/// <summary>
/// Derives the fighter read-models on top of <see cref="ISdeAccessor"/>: the kind/role/size classification and the
/// per-platform launchable set the Fighter Bay UI and the squadron-DPS engine consume. A thin derivation layer (like the
/// environment-beacon classifier), so the raw SDE access stays in <see cref="ISdeAccessor"/>.
/// </summary>
public interface IFighterAccessor
{
    /// <summary>The fighter read-model for a type, or null when the type is not a fighter (not one of the six category-87
    /// groups). Classifies kind/structure from the group and reads squadron size (2215), role (2270) and the attack
    /// multiplier (2226, the deals-damage gate).</summary>
    FighterType? GetFighterType(int typeId);

    /// <summary>The fighter types the given platform can launch: a carrier/supercarrier launches the ship fighters whose
    /// kind has a positive tube limit (light 2217 / support 2218 / heavy 2219), an Upwell structure (category 65)
    /// launches the matching Standup fighters. Ordered by kind then name. Empty when the platform has no fighter tubes.</summary>
    IReadOnlyList<FighterType> ListLaunchableFighters(int platformTypeId);
}
