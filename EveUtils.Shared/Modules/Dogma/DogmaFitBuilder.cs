using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// Pass 1 (design §3): turns a <see cref="FitInput"/> into the <see cref="DogmaFit"/> object graph. Seeds each item
/// with its SDE base attributes/group/category, and builds a skill item (level forced via <see cref="SkillSource"/>,
/// bypassing the pipeline) for every skill the ship or its modules require.
/// </summary>
public sealed class DogmaFitBuilder(IDogmaDataAccessor data) : ISingletonService
{
    private const int StructureCategoryId = 65;       // citadels / engineering complexes
    private const int StructureSkillGroupId = 1545;   // "Structure Management" — the only skills that affect a structure

    public DogmaFit Build(FitInput fit)
    {
        var ship = CreateItem(fit.ShipTypeId, ModuleState.Overload, isAlwaysOn: true);
        var mode = BuildTacticalMode(fit.ShipTypeId, fit.TacticalModeTypeId);
        var character = new DogmaItem(0, ModuleState.Overload, groupId: 0, categoryId: 0, isAlwaysOn: true, baseAttributes: []);
        var modules = fit.Modules.Select(BuildModule).ToList();
        var drones = (fit.Drones ?? []).Select(BuildDrone).ToList();
        var fighters = (fit.Fighters ?? []).Select(BuildFighter).ToList();
        var implants = (fit.Implants ?? []).Select(BuildImplant).ToList();
        var weather = fit.Weather is { } selectedWeather ? BuildWeatherBeacon(selectedWeather) : null;
        // A structure (citadel/engineering complex) is not piloted: only the owner's Structure-Management skills affect
        // it (routed via the structureID domain), never the ship-piloting skills (which would otherwise inflate its
        // HP via shipID). Its own role bonuses and Standup-module effects apply as normal.
        var structureOnly = data.GetCategoryId(fit.ShipTypeId) == StructureCategoryId;
        var skills = BuildSkills(fit.Skills, structureOnly);
        return new DogmaFit(ship, character, modules, skills, drones, implants, mode, weather, fighters);
    }

    // The selected environment/weather is an Effect-Beacon (group 920): its category-7 "system" effects modify the ship
    // via the shipID domain. It is injected exactly like an implant — a ship-anchored always-on source — so its effects
    // run through the normal pass-2 graph (always-on, so the category-7 state gate is bypassed). No selection → no source.
    private DogmaItem BuildWeatherBeacon(WeatherInput input) =>
        CreateItem(input.TypeId, ModuleState.Online, isAlwaysOn: true);

    // A Tactical Destroyer's stance mode (Defense by default — the standard import default) is a
    // ship-anchored always-on item: its passive effects route to the ship through the normal pass-2 graph and it
    // costs no slot/CPU/PG (it is not a module). Null for non-T3D ships.
    private DogmaItem? BuildTacticalMode(int shipTypeId, int? overrideModeTypeId) =>
        (overrideModeTypeId ?? data.GetDefaultTacticalModeTypeId(shipTypeId)) is { } modeTypeId
            ? CreateItem(modeTypeId, ModuleState.Online, isAlwaysOn: true)
            : null;

    // An implant is always on once plugged in (its bonuses are passive); it is a source only, so no quantity or charge.
    private DogmaItem BuildImplant(ImplantInput input) =>
        CreateItem(input.TypeId, ModuleState.Overload, isAlwaysOn: true);

    // A drone is active in space (its targetAttack effect is active-state); its count drives DPS, and its skill
    // bonuses arrive via OwnerRequiredSkillModifier (the character owns it), routed in pass 2.
    private DogmaItem BuildDrone(DroneInput input)
    {
        var drone = CreateItem(input.TypeId, ModuleState.Active, isAlwaysOn: false);
        drone.Quantity = input.Amount;
        return drone;
    }

    // A launched fighter squadron is active in space; its active fighter count (FighterInput.ActiveCount) scales its DPS,
    // and its skill/hull bonuses arrive via OwnerRequiredSkillModifier (char-owned, like a drone — see DogmaEffectCollector).
    private DogmaItem BuildFighter(FighterInput input)
    {
        var fighter = CreateItem(input.TypeId, ModuleState.Active, isAlwaysOn: false);
        fighter.Quantity = input.ActiveCount;
        return fighter;
    }

    private DogmaItem BuildModule(ModuleInput input)
    {
        var module = CreateItem(input.TypeId, input.State, isAlwaysOn: false);
        if (input.ChargeTypeId is { } chargeTypeId)
        {
            // The charge provides attributes the module reads via otherID; its own contribution is always available
            // (the module's effects gate on the module's state, so an offline module never reads the charge anyway).
            var charge = CreateItem(chargeTypeId, input.State, isAlwaysOn: true);
            module.Charge = charge;
            charge.Host = module;
        }
        return module;
    }

    private DogmaItem CreateItem(int typeId, ModuleState state, bool isAlwaysOn) =>
        new(typeId, state, data.GetGroupId(typeId) ?? 0, data.GetCategoryId(typeId) ?? 0, isAlwaysOn,
            data.GetBaseAttributes(typeId));

    // The all-V baseline injects every skill (skills carry the fitting/navigation/tanking bonuses, not just the
    // modules' required skills); a character snapshot injects its trained skills. Either way the level is forced. For a
    // structure only the Structure-Management group applies, so the set is filtered to it.
    private List<DogmaItem> BuildSkills(SkillSource skills, bool structureOnly)
    {
        IEnumerable<int> skillIds = skills.InjectsAllSkills ? data.GetSkillTypeIds() : skills.ExplicitSkillTypeIds;
        if (structureOnly)
            skillIds = skillIds.Where(id => data.GetGroupId(id) == StructureSkillGroupId);
        var result = new List<DogmaItem>();
        foreach (var skillId in skillIds)
        {
            var skill = CreateItem(skillId, ModuleState.Overload, isAlwaysOn: true);
            skill.Force(DogmaAttributeIds.SkillLevel, skills.LevelFor(skillId));
            result.Add(skill);
        }
        return result;
    }
}
