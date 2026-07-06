using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>
/// The engine's data-driven patches over the SDE: synthetic attributes, modifier patches for effects CCP ships empty
/// (or brand-new custom effects), and by-category type links. The patch pattern keeps special cases (propulsion,
/// missile skills, …) as data rather than hand-written calculator code,
/// so a future SDE that fills the gaps takes over silently. Applied once and memoised by <see cref="SqliteDogmaDataAccessor"/>;
/// no hot-path cost. Custom ids use the &gt; 45000 range (CCP reserves none — see ModifierInfo-Expertise §10).
/// </summary>
public static class DogmaPatches
{
    // Attribute ids the patches wire together.
    private const int Mass = 4;
    private const int Agility = 70;              // inertia modifier — drives align time
    private const int DamageMultiplier = 64;
    private const int CycleTime = 51;           // "speed" — module cycle time, reduced by rate-of-fire skills
    private const int EmDamage = 114;
    private const int ExplosiveDamage = 116;
    private const int KineticDamage = 117;
    private const int ThermalDamage = 118;
    private const int SignatureRadius = 552;
    private const int SignatureRadiusBonus = 554;
    private const int MassAddition = 796;
    private const int DamageMultiplierBonus = 292;
    private const int RofBonus = 293;           // negative per-level rate-of-fire bonus (level-scaled by effect 163)
    private const int Million = 65531;         // synthetic constant 1e6 (align-time divisor)
    private const int AlignTime = 65534;       // synthetic: -ln(0.25) * agility * mass / 1e6 (seconds)
    private const double AlignTimeConstant = 1.3862943611198906;   // -ln(0.25)
    private const int Cpu = 50;                // a module's cpu usage
    private const int CpuLoad = 49;            // the ship's running cpu total
    private const int Power = 30;              // a module's powergrid usage
    private const int PowerLoad = 15;          // the ship's running powergrid total
    private const int CpuPowerLoad = 65521;    // synthetic online effect: fold module cpu/power onto the ship load
    private const int ModuleCategory = 7;

    // A missile size-skill damage bonus (one of the four empty missile*DmgBonus effects): the owner's missiles
    // requiring the carrying skill get a post-percent on the damage type, from the skill's level-scaled bonus (attr 292).
    private static EffectPatch MissileDamage(int effectId, int damageAttributeId) =>
        new(effectId, EffectCategoryId: 0,
            [Mod(ModifierFunc.OwnerRequiredSkillModifier, ModifierDomain.CharId, 6, damageAttributeId, DamageMultiplierBonus)]);

    private const int ShipCategory = 6;

    private static ModifierInfo Mod(ModifierFunc func, ModifierDomain domain, int operation, int modified, int modifying,
        int? skill = null) => new(func, domain, operation, modified, modifying, GroupId: null, SkillTypeId: skill);

    public static IReadOnlyList<SyntheticAttribute> Attributes { get; } =
    [
        // Align time: the attribute starts at -ln(0.25); the effect multiplies in agility and mass and divides by a
        // million. Stackable so the multiplications are not penalised; lower is better.
        new(AlignTime, DefaultValue: AlignTimeConstant, Stackable: true, HighIsGood: false),
        new(Million, DefaultValue: 1_000_000, Stackable: true, HighIsGood: true)
    ];

    private static readonly Dictionary<int, EffectPatch> EffectPatches = new()
    {
        // Drone size-skill damage bonus (Medium Drone Operation / Gallente Drone Specialization carry this empty) —
        // boosts the damage multiplier of the owner's drones requiring the carrying skill (null = carrier convention).
        [1730] = new EffectPatch(1730, EffectCategoryId: 0,
        [
            Mod(ModifierFunc.OwnerRequiredSkillModifier, ModifierDomain.CharId, 6, DamageMultiplier, DamageMultiplierBonus)
        ]),

        // Afterburner (6731, empty): add the prop module's mass to the ship (data-driven). The velocity boost itself is
        // a code aggregate in DerivedStatsCalculator (speedFactor*speedBoostFactor/mass per module, stacking-penalised) —
        // the modifier framework cannot divide a per-module value by ship mass, so a shared synthetic attribute would
        // compound wrongly with more than one prop module.
        [6731] = new EffectPatch(6731, EffectCategoryId: 0,
        [
            Mod(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 2, Mass, MassAddition)
        ]),

        // Microwarpdrive (6730, empty): add mass plus the signature-radius penalty (both per-module-correct as modifiers);
        // the velocity boost is the same code aggregate as the afterburner.
        [6730] = new EffectPatch(6730, EffectCategoryId: 0,
        [
            Mod(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 2, Mass, MassAddition),
            Mod(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 6, SignatureRadius, SignatureRadiusBonus)
        ]),

        // Synthetic ship effect: align time = -ln(0.25) * agility * mass / 1e6. The attribute base is -ln(0.25); the
        // operator order (PostMul agility, PostMul mass, PostDiv million) folds the formula. Passive (always on the
        // ship). agility/mass are the resolved ship attributes, so module/skill inertia and mass changes flow in.
        [AlignTime] = new EffectPatch(AlignTime, EffectCategoryId: 0,
        [
            Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 4, AlignTime, Agility),
            Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 4, AlignTime, Mass),
            Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 5, AlignTime, Million)
        ]),

        // Missile size-skill damage (missile{Em/Explosive/Thermal/Kinetic}DmgBonus, all shipped empty) — Warhead
        // Upgrades carries the equivalent real modifierInfo; the size skills (Heavy Missiles, …) do not.
        [660] = MissileDamage(660, EmDamage),
        [661] = MissileDamage(661, ExplosiveDamage),
        [662] = MissileDamage(662, ThermalDamage),
        [668] = MissileDamage(668, KineticDamage),

        // Missile specialization rate of fire (selfRof, shipped empty, shared by all missile specializations): the
        // launchers requiring the specialization fire faster by the skill's level-scaled (negative) rofBonus.
        [1851] = new EffectPatch(1851, EffectCategoryId: 0,
        [
            Mod(ModifierFunc.LocationRequiredSkillModifier, ModifierDomain.ShipId, 6, CycleTime, RofBonus)
        ]),

        // cpuPowerLoad (custom, online): CCP ships no effect whose modifierInfo folds a module's cpu/power onto the
        // ship's running cpuLoad/powerLoad, so this patch supplies it. effectCategory 4 (online) means the existing state-gate
        // only counts a module that is online or higher — an offline module frees its load (in-game behaviour). Each
        // online module adds its cpu(50)/power(30) to the ship's cpuLoad(49)/powerLoad(15) via shipID ModAdd; attached
        // to every Module type (category 7) below. The free side (cpuFree/powerFree, 65520) stays unbuilt — we derive
        // free as output - load in the view, so the synthetic free attributes would be unused machinery.
        [CpuPowerLoad] = new EffectPatch(CpuPowerLoad, EffectCategoryId: 4,
        [
            Mod(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 2, CpuLoad, Cpu),
            Mod(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 2, PowerLoad, Power)
        ])
    };

    public static IReadOnlyList<TypeLink> TypeLinks { get; } =
    [
        new([ShipCategory], [AlignTime]),
        new([ModuleCategory], [CpuPowerLoad])
    ];

    /// <summary>The effect patch for an effect id (empty-effect merge or a new custom effect), if any.</summary>
    public static bool TryGetEffectPatch(int effectId, out EffectPatch patch) =>
        EffectPatches.TryGetValue(effectId, out patch!);

    /// <summary>Synthetic attribute metadata for a custom id, or null if it is a real SDE attribute.</summary>
    public static DogmaAttributeMeta? AttributeMeta(int attributeId)
    {
        foreach (var attribute in Attributes)
            if (attribute.AttributeId == attributeId)
                return attribute.ToMeta();
        return null;
    }

    /// <summary>Effect ids that type-links attach to a given inventory category (empty for most categories).</summary>
    public static IReadOnlyList<int> EffectIdsForCategory(int categoryId)
    {
        List<int>? result = null;
        foreach (var link in TypeLinks)
            if (link.Categories.Contains(categoryId))
                (result ??= []).AddRange(link.AttachEffectIds);
        return result ?? [];
    }
}
