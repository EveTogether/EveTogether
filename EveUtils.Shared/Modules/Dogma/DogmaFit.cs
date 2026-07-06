namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The object graph of a single fit (design §3): the ship, the character (host for skills/implants), the modules and
/// the skill items. Built by pass 1, modified by pass 2, evaluated by pass 3. One per calculation, then discarded.
/// </summary>
public sealed class DogmaFit(
    DogmaItem ship, DogmaItem character, IReadOnlyList<DogmaItem> modules, IReadOnlyList<DogmaItem> skills,
    IReadOnlyList<DogmaItem> drones, IReadOnlyList<DogmaItem> implants, DogmaItem? mode = null, DogmaItem? weather = null,
    IReadOnlyList<DogmaItem>? fighters = null)
{
    public DogmaItem Ship => ship;

    public DogmaItem Character => character;

    /// <summary>The active tactical mode (T3 destroyers): a ship-anchored, always-on source of the stance bonuses
    /// (resists/signature/speed/range). Null for other ships. Defaults to Defense.</summary>
    public DogmaItem? Mode => mode;

    public IReadOnlyList<DogmaItem> Modules => modules;

    public IReadOnlyList<DogmaItem> Skills => skills;

    /// <summary>The drones in space — char-owned, so they receive <c>OwnerRequiredSkillModifier</c> bonuses.</summary>
    public IReadOnlyList<DogmaItem> Drones => drones;

    /// <summary>The launched fighter squadrons — char-owned like drones, so the Fighters skill + hull fighter-damage
    /// bonuses reach them through <c>OwnerRequiredSkillModifier</c>. Each carries its squadron's active fighter count as
    /// <c>Quantity</c>, which scales its DPS.</summary>
    public IReadOnlyList<DogmaItem> Fighters => fighters ?? [];

    /// <summary>The implants in the character's slots — passive, char-anchored sources of bonuses to the ship/modules.</summary>
    public IReadOnlyList<DogmaItem> Implants => implants;

    /// <summary>The selected environment/weather: an Effect-Beacon source whose category-7 system effects modify
    /// the ship via the <c>shipID</c> domain. Null when no weather is selected — then it never enters the graph.</summary>
    public DogmaItem? Weather => weather;

    /// <summary>Every item in the graph, the order pass 2 collects effects in (ship, tactical mode, char, modules +
    /// their charges, drones, implants, weather, skills).</summary>
    public IEnumerable<DogmaItem> AllItems
    {
        get
        {
            yield return ship;
            if (mode is not null)
                yield return mode;
            yield return character;
            foreach (var module in modules)
            {
                yield return module;
                if (module.Charge is { } charge)
                    yield return charge;
            }
            foreach (var drone in drones)
                yield return drone;
            foreach (var fighter in Fighters)
                yield return fighter;
            foreach (var implant in implants)
                yield return implant;
            if (weather is not null)
                yield return weather;
            foreach (var skill in skills)
                yield return skill;
        }
    }
}
