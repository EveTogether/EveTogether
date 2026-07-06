namespace EveUtils.Shared.Modules.Dogma;

/// <summary>Well-known dogma attribute ids the engine references by name (CCP-stable). Kept tiny on purpose — only
/// the ids the kernel itself needs; everything else stays data-driven.</summary>
public static class DogmaAttributeIds
{
    /// <summary>skillLevel — a skill item's trained level, set as a forced value (operation 9 is skipped).</summary>
    public const int SkillLevel = 280;

    /// <summary>requiredSkill1..6 — the skill type ids a type requires, used to route required-skill modifiers.</summary>
    public static readonly int[] RequiredSkill = [182, 183, 184, 1285, 1289, 1290];

    /// <summary>requiredSkill1..6Level — the level each required skill must reach, index-aligned with
    /// <see cref="RequiredSkill"/>. Used by the fit validator to diff against a character's trained levels.</summary>
    public static readonly int[] RequiredSkillLevel = [277, 278, 279, 1286, 1287, 1288];

    // Character training attributes. A skill's training rate is primary + secondary/2 SP per minute, where
    // primary/secondary point at one of these five character attributes; attribute implants raise their effective value.
    public const int Charisma = 164;
    public const int Intelligence = 165;
    public const int Memory = 166;
    public const int Perception = 167;
    public const int Willpower = 168;

    // Attribute-enhancer implants carry their +stat on these "xxxBonus" attributes (175-179), NOT on the character
    // attributes (164-168) themselves — verified against the SDE dogmaAttributes (175 = "Charisma Modifier"). The
    // effective training attribute = base allocation + the matching implant bonus.
    public const int CharismaBonus = 175;
    public const int IntelligenceBonus = 176;
    public const int MemoryBonus = 177;
    public const int PerceptionBonus = 178;
    public const int WillpowerBonus = 179;

    public const int SkillPrimaryAttribute = 180;   // points at the character attribute (164-168) a skill trains on first
    public const int SkillSecondaryAttribute = 181; // ...and second
    public const int SkillTimeConstant = 275;       // "rank" — SP = 250 * rank * sqrt(32)^(level-1)

    /// <summary>Ship mass — a Type-row field seeded as a base attribute so module mass additions apply through the pipeline.</summary>
    public const int Mass = 4;

    // Fitting resources. cpuLoad/powerLoad are the ship-side running totals the cpuPowerLoad patch fills from each
    // online module's cpu/power (DogmaPatches) — state-gated, so an offline module frees its load.
    public const int CpuOutput = 48;
    public const int Cpu = 50;
    public const int CpuLoad = 49;
    public const int PowerOutput = 11;
    public const int Power = 30;
    public const int PowerLoad = 15;
    public const int MaxVelocity = 37;
    public const int ExplosionDelay = 281;      // missile flight time (ms); range = missile maxVelocity (37) * flightTime
    public const int SignatureRadius = 552;     // penalised by the microwarpdrive (velocityBoost patch)
    public const int AlignTime = 65534;         // synthetic: -ln(0.25) * agility * mass / 1e6 (DogmaPatches)

    // Propulsion-module speed formula (afterburner/microwarpdrive).
    public const int SpeedFactor = 20;          // raw thrust factor (boosted by Acceleration Control)
    public const int SpeedBoostFactor = 567;
    public const int MassAddition = 796;        // mass a module adds to the ship

    // Turret DPS: cycle time + damage multiplier on the weapon, four damage types on the loaded charge.
    public const int CycleTime = 51;            // "speed" — ms per shot
    public const int DamageMultiplier = 64;
    // Entropic disintegrator (Triglavian/Precursor) spool-up: the damage multiplier ramps per cycle up to this max bonus.
    // Presence of this attr (not effect 6995, which also sits on NPCs) is the data-driven ramp signal; max DPS = base * (1 + value).
    public const int DamageMultiplierBonusMax = 2734;
    public const int EmDamage = 114;
    public const int ExplosiveDamage = 116;
    public const int KineticDamage = 117;
    public const int ThermalDamage = 118;
    public static readonly int[] DamageTypes = [EmDamage, ExplosiveDamage, KineticDamage, ThermalDamage];

    // Fighter abilities carry their damage on per-ability attributes, not the universal 114/116/117/118 — EVE stores
    // fighter damage differently, so the fighter DPS pass reads these (same formula shape as the weapon/drone pass, just a
    // different attribute set). Primary "attack" ability (every damage-dealing fighter) + the heavy fighter's secondary
    // missile salvo. All resolved through the pipeline so the Fighters skill / hull bonuses fold in.
    public const int FighterAttackDamageMultiplier = 2226;   // 0 on support/superiority fighters (the deals-damage gate)
    public const int FighterAttackEm = 2227;
    public const int FighterAttackThermal = 2228;
    public const int FighterAttackKinetic = 2229;
    public const int FighterAttackExplosive = 2230;
    public const int FighterAttackDuration = 2233;           // ms
    public const int FighterMissilesDamageMultiplier = 2130; // heavy fighters' secondary salvo
    public const int FighterMissilesEm = 2131;
    public const int FighterMissilesThermal = 2132;
    public const int FighterMissilesKinetic = 2133;
    public const int FighterMissilesExplosive = 2134;
    public const int FighterMissilesDuration = 2182;         // ms
    public const int FighterSquadronRole = 2270;             // 1=Superiority,2=LightAttack,4=HeavyAttack,5=LongRange — drives reload
    public const int FighterRefuelTime = 2426;               // ms, the fixed part of an ability's reload
    // Engagement envelope (informational tooltip — target-less sim, no applied DPS): attack optimal/falloff and the
    // secondary salvo's flight range.
    public const int FighterAttackOptimalRange = 2236;       // m
    public const int FighterAttackFalloffRange = 2237;       // m
    public const int FighterMissilesRange = 2149;            // m
    // Support-fighter EWAR (informational — the sim is target-less): each support type carries exactly one of these
    // ability sets. Presence of the strength attribute identifies the EWAR kind.
    public const int FighterNeutAmount = 2211;               // GJ neutralised
    public const int FighterNeutOptimal = 2209;
    public const int FighterNeutFalloff = 2210;
    public const int FighterEcmStrength = 2246;              // jam strength (all four sensor types are equal)
    public const int FighterEcmOptimal = 2221;
    public const int FighterEcmFalloff = 2222;
    public const int FighterPointStrength = 2205;            // warp disruption point strength
    public const int FighterPointRange = 2204;
    public const int FighterWebSpeedPenalty = 2184;          // stasis webifier speed penalty (%)
    public const int FighterWebOptimal = 2186;
    public const int FighterWebFalloff = 2187;

    // Per-module tooltip readout. Turret/drone engagement envelope: optimal range, falloff and tracking — all
    // resolved through the pipeline so skills/ship/rig bonuses fold in. Local-repair amounts per cycle (the cycle is the
    // duration, 73): armor/shield/hull repaired each activation; rep/s = amount / (duration_ms / 1000).
    public const int MaxRange = 54;             // optimal range (m)
    public const int Falloff = 158;             // falloff range (m)
    public const int TrackingSpeed = 160;       // turret/drone tracking (rad/s)
    public const int ArmorRepairAmount = 84;    // armorDamageAmount — armor HP repaired per cycle
    public const int ShieldBoostAmount = 68;    // shieldBonus — shield HP boosted per cycle
    public const int HullRepairAmount = 83;     // structureDamageAmount — hull HP repaired per cycle

    // Drones: the ship's available drone bandwidth and a drone's bandwidth use. Active drones are capped to 5 and must
    // fit within the ship bandwidth (a drone in the bay beyond that is not "in space" and contributes no DPS).
    public const int DroneBandwidth = 1271;
    public const int DroneBandwidthUse = 1272;

    // Mining yield: m³ harvested each duration (73) cycle. The same attribute backs mining lasers, strip miners, ice and
    // gas harvesters and mining drones, so a positive miningAmount is what marks an item as mining. The cycle is the
    // duration (73), not the turret speed (51).
    public const int MiningAmount = 77;

    // Capacitor: ship capacity + recharge, and per-module usage. A module's activation cycle is the larger of speed (51)
    // and duration (73); reactivation delay extends it. capacitorBonus is the cap a charge (cap booster) restores.
    public const int CapacitorCapacity = 482;
    public const int RechargeRate = 55;         // ms for a full capacitor recharge curve
    public const int CapacitorNeed = 6;         // GJ a module spends per activation
    public const int Duration = 73;             // activation cycle for most modules
    public const int ReactivationDelay = 669;
    public const int CapacitorBonus = 67;       // GJ a cap-booster charge restores
    public const int ReloadTime = 1795;         // ms to reload a weapon's clip or a cap booster's charges
    public const int PowerTransferAmount = 90;  // GJ an energy nosferatu transfers from a target into our capacitor

    // EHP layers: hit-point attribute + its four damage resonances (em, thermal, kinetic, explosive).
    public const int ShieldCapacity = 263;
    public const int ArmorHp = 265;
    public const int StructureHp = 9;
    public static readonly int[] ShieldResonance = [271, 274, 273, 272];
    public static readonly int[] ArmorResonance = [267, 270, 269, 268];
    public static readonly int[] StructureResonance = [113, 110, 109, 111];

    // Weapon hardpoints on the hull — how many turret / launcher mounts the ship has, for the in-game hardpoint indicators
    // above the high-slot ring. These are the hull totals (a fitted turret/launcher consumes one).
    public const int LauncherHardpoints = 101;   // launcherSlotsLeft
    public const int TurretHardpoints = 102;     // turretSlotsLeft
}
