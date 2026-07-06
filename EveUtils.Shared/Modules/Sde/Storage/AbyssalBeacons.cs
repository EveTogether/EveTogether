using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>
/// The synthetic abyssal weather beacons. Group 1983 "Abyssal Environment" carries no dogma in
/// the SDE — CCP applies the in-site effect server-side — so the five abyssal weathers are reconstructed here as
/// <see cref="SyntheticBeacon"/>s using the EVE University numbers, plumbed through the <b>real</b> single-purpose
/// category-7 system effects that the wormhole/metaliminal beacons use (so no new modifierInfo is invented). Each
/// weather is one flat bonus plus one penalty that scales over three tier buckets (T1-2 / T3-4 / T5-6, research §2b).
///
/// <para>The abyssal penalty is not a fixed per-tier value: EVE University documents it as a random roll per site
/// (T0-3 → −30%/−50%, T4-6 → −50%/−70%), so the picker offers the three discrete penalty strengths {−30/−50/−70%} as an
/// explicit user choice rather than a tier lookup. The bonus is a flat +50% on every weather.</para>
/// <para><b>Magnitudes are EVE University values cross-checked against a second community source (the abyssal-magnitudes
/// spike), but unconfirmed against the SDE — validate in-game. The plumbing (which effect/attribute
/// applies where) is taken verbatim from the live wormhole/metaliminal beacons and is exact.</b></para>
/// </summary>
public static class AbyssalBeacons
{
    public const int GroupId = 920;        // "Effect Beacon" — the engine reads group/category for stacking
    public const int CategoryId = 2;       // category of group 920 (Effect Beacon)
    public const string Category = "Abyssal";

    private const int FirstTypeId = 45100; // synthetic ids in the > 45000 custom range (CCP reserves none)
    private const int SortBase = 4000;     // after wormhole (1000) / metaliminal (2000) / Triglavian (3000)

    // ── Real single-purpose category-7 system effects + the beacon attribute each reads (verified from the live SDE) ──
    // Multiplier effects (operation 4): the attribute is a factor on the ship attribute.
    private const int ShieldHpEff = 3992, ShieldHpAttr = 146;            // shieldCapacityMultiplier
    private const int ArmorHpEff = 5913, ArmorHpAttr = 148;             // armorHPMultiplier
    private const int MaxVelocityEff = 4003, MaxVelocityAttr = 1470;     // maxVelocityMultiplier
    private const int CapRechargeEff = 4091, CapRechargeAttr = 1500;     // rechargeRateMultiplier (lower = faster regen)
    // Bonus-percent effect (operation 6): the attribute is a +% applied to the ship attribute.
    private const int ScanResEff = 8082, ScanResAttr = 566;             // scanResolutionBonus

    // The three discrete penalty strengths (random per site in-game; an explicit user choice here). Bonus is always +50%.
    private static readonly double[] PenaltyStrengths = [30.0, 50.0, 70.0];
    private const double FlatBonusMultiplier = 1.50;   // +50% for the op-4 multiplier bonuses (shield/armor HP, velocity)
    private const double CapRechargeMultiplier = 0.50; // −50% capacitor recharge time = double regen (op-4 multiplier)
    private const double ScanResolutionBonusPercent = 50.0; // +50% scan resolution (op-6 bonus percent)

    private enum DamageType { Em, Thermal, Kinetic, Explosive }

    // Resist penalty per damage type applied to all three tank layers (armor/shield/hull), exactly as the metaliminal
    // storms do: each pair is (effectId, beaconAttributeId), op 6 — a positive value worsens the resonance (a penalty,
    // confirmed against Pulsar's armorEmDamageResistanceBonus).
    private static (int Eff, int Attr)[] ResistLayers(DamageType type) => type switch
    {
        DamageType.Em        => [(3996, 1465), (4135, 1489), (8075, 984)],
        DamageType.Thermal   => [(3999, 1467), (4138, 1492), (8076, 987)],
        DamageType.Kinetic   => [(3998, 1466), (4137, 1491), (8077, 986)],
        DamageType.Explosive => [(3997, 1468), (4136, 1490), (8078, 985)],
        _ => [],
    };

    // A flat +50% bonus = one (effect, attribute, value); plus a damage-type resist penalty applied to all three layers.
    private sealed record WeatherSpec(string Name, int BonusEff, int BonusAttr, double BonusValue, DamageType ResistPenalty);

    // The four resist-penalty abyssal weathers. Each carries a flat +50% bonus (verified, 2 sources) and a resist penalty
    // whose strength the user picks. Dark Matter Field is deferred: its turret optimal+falloff penalty has no
    // single-purpose system effect to reuse and needs the (Magnetar-style) range-modifier shape + validation.
    private static readonly WeatherSpec[] Specs =
    [
        new("Electrical", CapRechargeEff, CapRechargeAttr, CapRechargeMultiplier, DamageType.Em),       // −50% cap recharge time
        new("Exotic", ScanResEff, ScanResAttr, ScanResolutionBonusPercent, DamageType.Kinetic),         // +50% scan resolution
        new("Firestorm", ArmorHpEff, ArmorHpAttr, FlatBonusMultiplier, DamageType.Thermal),             // +50% armor HP
        new("Gamma", ShieldHpEff, ShieldHpAttr, FlatBonusMultiplier, DamageType.Explosive),             // +50% shield HP
    ];

    private static IReadOnlyList<SyntheticBeacon> Build()
    {
        var beacons = new List<SyntheticBeacon>();
        var typeId = FirstTypeId;
        var weatherIndex = 0;
        foreach (var spec in Specs)
        {
            for (var strength = 0; strength < PenaltyStrengths.Length; strength++)
            {
                var attributes = new List<SdeDogmaAttribute> { new(spec.BonusAttr, spec.BonusValue) };
                var effectIds = new List<int> { spec.BonusEff };
                foreach (var (eff, attr) in ResistLayers(spec.ResistPenalty))
                {
                    attributes.Add(new SdeDogmaAttribute(attr, PenaltyStrengths[strength]));
                    effectIds.Add(eff);
                }
                beacons.Add(new SyntheticBeacon(typeId++, $"Abyssal {spec.Name} (−{PenaltyStrengths[strength]:0}% resist)",
                    Category, SortBase + weatherIndex * 10 + strength, attributes, effectIds));
            }
            weatherIndex++;
        }
        return beacons;
    }

    /// <summary>All synthetic abyssal beacons (5 weathers × 3 tier buckets once seeded).</summary>
    public static IReadOnlyList<SyntheticBeacon> All { get; } = Build();

    private static readonly Dictionary<int, SyntheticBeacon> ById = All.ToDictionary(beacon => beacon.TypeId);

    /// <summary>The synthetic beacon for a type id, or null when it is not an abyssal beacon.</summary>
    public static SyntheticBeacon? Get(int typeId) => ById.GetValueOrDefault(typeId);

    /// <summary>The picker entries for the abyssal beacons (engine data lives on the beacons themselves).</summary>
    public static IEnumerable<SdeEnvironmentBeacon> EnvironmentBeacons() =>
        All.Select(beacon => new SdeEnvironmentBeacon(beacon.TypeId, beacon.DisplayName, beacon.Category, beacon.SortOrder));
}
