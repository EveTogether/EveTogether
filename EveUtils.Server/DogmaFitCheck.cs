using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server;

/// <summary>
/// Validates the dogma engine against a real reference fit (--dogma-fit-test): parses the
/// exported EFT through the real SDE, runs the calculator at all-level-5, and compares CPU/power/velocity/EHP to
/// the reference numbers for the same fit. The reference is an oracle (its EFT + its stats, fetched via Playwright)
/// — this is the autonomous cross-check that settles accuracy where it can't be hand-verified. Diagnostic output;
/// exit 0 always, mismatches are printed with their delta.
/// </summary>
public static class DogmaFitCheck
{
    // Thrasher "AA 314A fit" (reference fit ca9b4a60-...), EFT read via Playwright.
    private const string Eft = """
        [Thrasher, AA 314A]
        Gyrostabilizer II
        Gyrostabilizer II

        1MN Y-S8 Compact Afterburner
        Small Shield Extender II
        EM Shield Amplifier II

        280mm Howitzer Artillery II
        280mm Howitzer Artillery II
        280mm Howitzer Artillery II
        280mm Howitzer Artillery II
        280mm Howitzer Artillery II
        280mm Howitzer Artillery II
        280mm Howitzer Artillery II

        Small Projectile Ambit Extension I
        Small Projectile Ambit Extension I
        Small Ancillary Current Router I
        """;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE Together dogma-fit check (engine vs EVE Workbench, all-level-5) ==");

        var importResult = await services.GetRequiredService<ISdeImporter>().EnsureUpToDateAsync();
        if (!importResult.Success)
        {
            Console.WriteLine($"FAIL: SDE not available ({importResult.Error})");
            return 1;
        }

        var sde = services.GetRequiredService<ISdeAccessor>();
        var parsed = services.GetRequiredService<IFitTextImporter>().Import(Eft);
        if (!parsed.Success)
        {
            Console.WriteLine($"FAIL: could not parse the EFT ({parsed.Error})");
            return 1;
        }

        // Modules = the slotted items (charges have no slot type); all set Active so the prop mod + tank apply.
        var modules = parsed.Fit!.Items
            .Where(item => sde.GetSlotType(item.TypeId) != SdeSlotType.None)
            .Select(item => new ModuleInput(item.TypeId, ModuleState.Active))
            .ToList();
        Console.WriteLine($"  parsed ship {parsed.Fit.ShipTypeId} + {modules.Count} fitted modules");

        var result = await services.GetRequiredService<IDogmaCalculator>()
            .CalculateAsync(new FitInput(parsed.Fit.ShipTypeId, modules, SkillSource.AllLevelFive));
        var derived = result.Derived;

        // The reference displayed numbers for this fit (read via Playwright).
        Compare("CPU used", derived.CpuUsed, 210.0, 0.02);
        Compare("CPU output", derived.CpuOutput, 212.5, 0.02);
        Compare("Power used", derived.PowerUsed, 90.4, 0.02);
        Compare("Power output", derived.PowerOutput, 96.2, 0.02);
        Compare("Max velocity", derived.MaxVelocity, 714.0, 0.02);
        Compare("Shield EHP", derived.ShieldEhp, 2710.0, 0.05);
        Compare("Armor EHP", derived.ArmorEhp, 1390.0, 0.05);
        Compare("Structure EHP", derived.StructureEhp, 1400.0, 0.05);
        Compare("Total EHP", derived.Ehp, 5500.0, 0.05);

        await RunThoraxAsync(services, sde);
        await RunMissileAsync(services, sde);
        await RunMicrowarpdriveAsync(services, sde);
        await RunImplantsAsync(services, sde);
        await RunOverloadAsync(services, sde);
        await RunCapacitorAsync(services, sde);

        Console.WriteLine("\n(diagnostic — see deltas above)");
        return 0;
    }

    // Capacitor cross-check, all-level-5. An unstable fit: a Thorax with a 50MN Microwarpdrive II, a Medium Armor
    // Repairer II and 5x 200mm Railgun II, all active. The MWD + repairer drain more than the capacitor regenerates, so
    // the discrete-event simulator reports the depletes-in time rather than a stable percentage — reference: capacity 1450,
    // depletes in 40.0s. (The stable case is checked on the VC-01 Thorax above.)
    private static async Task RunCapacitorAsync(IServiceProvider services, ISdeAccessor sde)
    {
        Console.WriteLine("\n== Capacitor unstable (engine vs pyfa-headless, all-level-5) ==");

        int Type(string name)
        {
            if (sde.TryGetTypeId(name, out var typeId))
                return typeId;
            Console.WriteLine($"  MISSING type: {name}");
            return 0;
        }

        var antimatter = Type("Federation Navy Antimatter Charge M");
        var modules = new List<ModuleInput>
        {
            new(Type("50MN Microwarpdrive II"), ModuleState.Active),
            new(Type("Medium Armor Repairer II"), ModuleState.Active),
        };
        for (var i = 0; i < 5; i++)
            modules.Add(new ModuleInput(Type("200mm Railgun II"), ModuleState.Active, antimatter));

        var result = await services.GetRequiredService<IDogmaCalculator>()
            .CalculateAsync(new FitInput(Type("Thorax"), modules, SkillSource.AllLevelFive));
        var derived = result.Derived;

        Compare("Cap capacity", derived.CapacitorCapacity, 1450.0, 0.02);
        Console.WriteLine($"  [{(derived.CapacitorStable ? "~~" : "OK")}] cap stable     mine={derived.CapacitorStable} (expected False)");
        Compare("Depletes in s", derived.CapacitorDepletesInSeconds, 40.0, 0.05);

        await RunCapacitorInjectorAsync(services, sde);
    }

    // Capacitor with an injector: a Thorax with a Medium Capacitor Booster II loaded with Navy Cap Booster 400 + two
    // Medium Armor Repairer IIs. The booster restores cap from its charges (an intermittent fill with a reload), keeping
    // the fit stable where the reps alone would not be — reference: recharge 37.77, load 35.56, stable at 31.24%.
    private static async Task RunCapacitorInjectorAsync(IServiceProvider services, ISdeAccessor sde)
    {
        Console.WriteLine("\n== Capacitor with injector (engine vs pyfa-headless, all-level-5) ==");

        int Type(string name)
        {
            if (sde.TryGetTypeId(name, out var typeId))
                return typeId;
            Console.WriteLine($"  MISSING type: {name}");
            return 0;
        }

        var modules = new List<ModuleInput>
        {
            new(Type("Medium Capacitor Booster II"), ModuleState.Active, Type("Navy Cap Booster 400")),
            new(Type("Medium Armor Repairer II"), ModuleState.Active),
            new(Type("Medium Armor Repairer II"), ModuleState.Active),
        };

        var result = await services.GetRequiredService<IDogmaCalculator>()
            .CalculateAsync(new FitInput(Type("Thorax"), modules, SkillSource.AllLevelFive));
        var derived = result.Derived;

        Compare("Cap recharge", derived.CapacitorRecharge, 37.7730, 0.02);   // passive peak + injector fill
        Compare("Cap used", derived.CapacitorUsed, 35.5556, 0.02);
        Console.WriteLine($"  [{(derived.CapacitorStable ? "OK" : "~~")}] cap stable     mine={derived.CapacitorStable} (expected True)");
        Compare("Cap stable %", derived.CapacitorStablePercent, 31.2422, 0.05);
    }

    // Overheating cross-check, all-level-5. Both overload effects are real SDE category-5 self-modifiers (no patch); the
    // engine activates them only in the overload state. (1) A Thorax + 50MN Microwarpdrive II overloaded: effect 3175
    // raises speedFactor +50%, so velocity beats the active MWD (2148.05) — reference 3065.83. (2) A Caracal with overloaded
    // Heavy Missile Launcher IIs: effect 3001 cuts cycle 15%, so missile DPS beats the active Caracal (165.32) —
    // reference 194.49.
    private static async Task RunOverloadAsync(IServiceProvider services, ISdeAccessor sde)
    {
        Console.WriteLine("\n== Overheating (engine vs pyfa-headless, all-level-5) ==");

        int Type(string name)
        {
            if (sde.TryGetTypeId(name, out var typeId))
                return typeId;
            Console.WriteLine($"  MISSING type: {name}");
            return 0;
        }

        var mwd = await services.GetRequiredService<IDogmaCalculator>().CalculateAsync(new FitInput(
            Type("Thorax"), [new ModuleInput(Type("50MN Microwarpdrive II"), ModuleState.Overload)], SkillSource.AllLevelFive));
        Compare("MWD velocity", mwd.Derived.MaxVelocity, 3065.83, 0.02);

        var scourge = Type("Scourge Heavy Missile");
        var launchers = new List<ModuleInput>();
        for (var i = 0; i < 5; i++)
            launchers.Add(new ModuleInput(Type("Heavy Missile Launcher II"), ModuleState.Overload, scourge));
        var caracal = await services.GetRequiredService<IDogmaCalculator>()
            .CalculateAsync(new FitInput(Type("Caracal"), launchers, SkillSource.AllLevelFive));
        Compare("Missile DPS", caracal.Derived.MissileDps, 194.49, 0.02);
    }

    // Implant cross-check: a Thorax with 5x 200mm Railgun II + three implants in distinct slots, all-level-5. Each
    // implant carries real SDE modifierInfo (no patch): shield management (+5% shieldCapacity, ItemModifier shipID),
    // navigation (+5% maxVelocity, ItemModifier shipID) and surgical strike (+5% turret damageMultiplier, a
    // LocationRequiredSkillModifier on Gunnery). Distinct implant slots, so no slot conflict — the fit model owns slot
    // uniqueness, not the dogma calculator. Validates that char-anchored implant sources route to the ship and to
    // skill-requiring modules. reference: shield EHP 2172, velocity 328.12, weapon DPS 271.44.
    private static async Task RunImplantsAsync(IServiceProvider services, ISdeAccessor sde)
    {
        Console.WriteLine("\n== Thorax + implants (engine vs pyfa-headless, all-level-5) ==");

        int Type(string name)
        {
            if (sde.TryGetTypeId(name, out var typeId))
                return typeId;
            Console.WriteLine($"  MISSING type: {name}");
            return 0;
        }

        var antimatter = Type("Federation Navy Antimatter Charge M");
        var modules = new List<ModuleInput>();
        for (var i = 0; i < 5; i++)
            modules.Add(new ModuleInput(Type("200mm Railgun II"), ModuleState.Active, antimatter));

        var implants = new List<ImplantInput>
        {
            new(Type("Zainou 'Gnome' Shield Management SM-705")),
            new(Type("Eifyr and Co. 'Rogue' Navigation NN-605")),
            new(Type("Eifyr and Co. 'Gunslinger' Surgical Strike SS-905")),
        };

        var result = await services.GetRequiredService<IDogmaCalculator>()
            .CalculateAsync(new FitInput(Type("Thorax"), modules, SkillSource.AllLevelFive, Implants: implants));
        var derived = result.Derived;

        Compare("Shield EHP", derived.ShieldEhp, 2172.0, 0.02);
        Compare("Max velocity", derived.MaxVelocity, 328.12, 0.02);
        Compare("Turret DPS", derived.TurretDps, 271.44, 0.02);
    }

    // Microwarpdrive cross-check: a Thorax with a single 50MN Microwarpdrive II active, all-level-5. The MWD effect
    // (6730, shipped empty) is patched data-driven through the velocityBoost synthetic attribute — it boosts velocity
    // and adds the signature-radius penalty (signatureRadius *= 1 + signatureRadiusBonus/100). reference: velocity 2148.05,
    // signature 720.
    private static async Task RunMicrowarpdriveAsync(IServiceProvider services, ISdeAccessor sde)
    {
        Console.WriteLine("\n== Thorax + 50MN Microwarpdrive II (engine vs pyfa-headless, all-level-5) ==");

        int Type(string name)
        {
            if (sde.TryGetTypeId(name, out var typeId))
                return typeId;
            Console.WriteLine($"  MISSING type: {name}");
            return 0;
        }

        var modules = new List<ModuleInput> { new(Type("50MN Microwarpdrive II"), ModuleState.Active) };

        var result = await services.GetRequiredService<IDogmaCalculator>()
            .CalculateAsync(new FitInput(Type("Thorax"), modules, SkillSource.AllLevelFive));
        var derived = result.Derived;

        Compare("Max velocity", derived.MaxVelocity, 2148.05, 0.02);
        Compare("Signature", derived.SignatureRadius, 720.0, 0.02);
    }

    // Missile cross-check: a Caracal with 5x Heavy Missile Launcher II + Scourge Heavy Missile, all-level-5. Damage sits
    // on the charge (Warhead Upgrades real + the patched size-skill bonuses) and the cycle is reduced by the rate-of-fire
    // skills (Missile Launcher Operation / Rapid Launch / Heavy Missile Specialization) + the Caracal hull bonus, all
    // data-driven — the reference weapon DPS for this fit is 165.32.
    private static async Task RunMissileAsync(IServiceProvider services, ISdeAccessor sde)
    {
        Console.WriteLine("\n== Caracal missiles (engine vs pyfa-headless, all-level-5) ==");

        int Type(string name)
        {
            if (sde.TryGetTypeId(name, out var typeId))
                return typeId;
            Console.WriteLine($"  MISSING type: {name}");
            return 0;
        }

        var scourge = Type("Scourge Heavy Missile");
        var modules = new List<ModuleInput>();
        for (var i = 0; i < 5; i++)
            modules.Add(new ModuleInput(Type("Heavy Missile Launcher II"), ModuleState.Active, scourge));

        var result = await services.GetRequiredService<IDogmaCalculator>()
            .CalculateAsync(new FitInput(Type("Caracal"), modules, SkillSource.AllLevelFive));

        Compare("Missile DPS", result.Derived.MissileDps, 165.32, 0.02);
    }

    // VC-01 Thorax, all-level-5, loaded Fed Navy Antimatter. The reference oracle here is a headless calculator
    // (gold standard, == in-game), validated to reproduce a known Rifter fit exactly. Built by type name so it mirrors
    // the reference oracle's explicit module list one-for-one. Drones (5x Hammerhead II) are a later phase, so the turret
    // DPS is compared against the reference *weapon* DPS, not its total.
    private static async Task RunThoraxAsync(IServiceProvider services, ISdeAccessor sde)
    {
        Console.WriteLine("\n== VC-01 Thorax (engine vs pyfa-headless, all-level-5) ==");

        int Type(string name)
        {
            if (sde.TryGetTypeId(name, out var typeId))
                return typeId;
            Console.WriteLine($"  MISSING type: {name}");
            return 0;
        }

        var antimatter = Type("Federation Navy Antimatter Charge M");
        var modules = new List<ModuleInput>
        {
            new(Type("Magnetic Field Stabilizer II"), ModuleState.Active),
            new(Type("Magnetic Field Stabilizer II"), ModuleState.Active),
            new(Type("Vortex Compact Magnetic Field Stabilizer"), ModuleState.Active),
            new(Type("Vortex Compact Magnetic Field Stabilizer"), ModuleState.Active),
            new(Type("Damage Control II"), ModuleState.Active),
            new(Type("10MN Monopropellant Enduring Afterburner"), ModuleState.Active),
            new(Type("Large F-S9 Regolith Compact Shield Extender"), ModuleState.Active),
            new(Type("Large F-S9 Regolith Compact Shield Extender"), ModuleState.Active),
            new(Type("Medium Shield Extender II"), ModuleState.Active),
            new(Type("Medium EM Shield Reinforcer I"), ModuleState.Active),
            new(Type("Medium Core Defense Field Extender I"), ModuleState.Active),
            new(Type("Medium Core Defense Field Extender I"), ModuleState.Active),
        };
        for (var i = 0; i < 5; i++)
            modules.Add(new ModuleInput(Type("200mm Railgun II"), ModuleState.Active, antimatter));

        var drones = new List<DroneInput> { new(Type("Hammerhead II"), 5) };

        var result = await services.GetRequiredService<IDogmaCalculator>()
            .CalculateAsync(new FitInput(Type("Thorax"), modules, SkillSource.AllLevelFive, drones));
        var derived = result.Derived;

        Compare("CPU used", derived.CpuUsed, 408.75, 0.02);
        Compare("CPU output", derived.CpuOutput, 412.50, 0.02);
        Compare("Power used", derived.PowerUsed, 1034.50, 0.02);
        Compare("Power output", derived.PowerOutput, 1075.00, 0.02);
        Compare("Max velocity", derived.MaxVelocity, 762.39, 0.02);     // reference all-V (the in-game shots
                                                                         // show 697 — the author's non-all-V character)
        Compare("Align time", derived.AlignTime, 7.4647, 0.02);         // -ln(0.25) * agility * mass / 1e6
        Compare("Shield EHP", derived.ShieldEhp, 19474.0, 0.05);
        Compare("Armor EHP", derived.ArmorEhp, 3486.0, 0.05);
        Compare("Structure EHP", derived.StructureEhp, 4975.0, 0.05);
        Compare("Total EHP", derived.Ehp, 27935.0, 0.05);
        Compare("Turret DPS", derived.TurretDps, 441.52, 0.02);          // reference weapon DPS
        Compare("Drone DPS", derived.DroneDps, 158.40, 0.02);            // reference drone DPS (5x Hammerhead II)
        Compare("Total DPS", derived.TotalDps, 599.92, 0.02);            // reference weapon + drone
        Compare("Cap capacity", derived.CapacitorCapacity, 1812.50, 0.02);
        Compare("Cap recharge", derived.CapacitorRecharge, 11.6860, 0.02);
        Compare("Cap used", derived.CapacitorUsed, 9.0515, 0.02);
        Compare("Cap stable %", derived.CapacitorStablePercent, 53.9007, 0.02);   // stable: settles at 53.9%

        Console.WriteLine($"  diag: sig={result.ShipAttribute(552):0.##}m  (mass lives on Type.mass, not dogma attr 4)");
    }

    private static void Compare(string label, double mine, double ewb, double tolerance)
    {
        var delta = ewb == 0 ? 0 : (mine - ewb) / ewb;
        var ok = Math.Abs(delta) <= tolerance;
        Console.WriteLine($"  [{(ok ? "OK" : "~~")}] {label,-14} mine={mine,10:0.##}  ewb={ewb,9:0.##}  delta={delta * 100,7:0.0}%");
    }
}
