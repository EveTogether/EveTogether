using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Fighters;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server;

/// <summary>
/// Runs the dogma engine over an arbitrary EFT file and prints the derived stats (--dogma-eft &lt;file&gt;), for the
/// validation campaign against reference fit stats (plans/dogma-validation-testset.md). Parses via the real EFT importer, maps the
/// assembled fit to a <see cref="FitInput"/> (modules + loaded charges by slot flag, drones, and implants pulled out
/// of cargo by category), then calculates at all-level-5. Mechanics the engine does not model yet (subsystems beyond
/// their effects, T3 modes, fighters, bastion, command bursts) simply do not contribute — the gap shows up against
/// the reference gold. Diagnostic; always exits 0.
/// </summary>
public static class DogmaEftCheck
{
    private const int ImplantCategoryId = 20;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var path = ArgValue(args, "--dogma-eft");
        if (path is null || !File.Exists(path))
        {
            Console.WriteLine($"FAIL: pass an EFT file path: --dogma-eft <file> (got '{path}')");
            return 0;
        }

        var importResult = await services.GetRequiredService<ISdeImporter>().EnsureUpToDateAsync();
        if (!importResult.Success)
        {
            Console.WriteLine($"FAIL: SDE not available ({importResult.Error})");
            return 0;
        }

        var sde = services.GetRequiredService<ISdeAccessor>();
        var parsed = services.GetRequiredService<IFitTextImporter>().Import(await File.ReadAllTextAsync(path));
        if (!parsed.Success)
        {
            Console.WriteLine($"FAIL: could not parse the EFT ({parsed.Error})");
            return 0;
        }

        var fit = parsed.Fit!;
        var data = services.GetRequiredService<EveUtils.Shared.Modules.Sde.IDogmaDataAccessor>();
        var (modules, drones, implants) = MapFit(fit, sde, data);
        var fighters = BuildFighters(fit, sde);

        var result = await services.GetRequiredService<IDogmaCalculator>()
            .CalculateAsync(new FitInput(fit.ShipTypeId, modules, SkillSource.AllLevelFive, drones, implants,
                Fighters: fighters));
        var d = result.Derived;

        sde.TryGetTypeName(fit.ShipTypeId, out var shipName);
        Console.WriteLine($"=== {shipName} | {fit.Name} ===");
        Console.WriteLine($"parsed: {modules.Count} modules, {drones.Count} drones, {implants.Count} implants" +
                          (parsed.Warnings.Count > 0 ? $" ({parsed.Warnings.Count} warnings)" : ""));
        Console.WriteLine($"CPU:          {d.CpuUsed:0.##} / {d.CpuOutput:0.##}");
        Console.WriteLine($"PowerGrid:    {d.PowerUsed:0.##} / {d.PowerOutput:0.##}");
        Console.WriteLine($"Max Velocity: {d.MaxVelocity:0.##} m/s");
        Console.WriteLine($"Sig Radius:   {d.SignatureRadius:0.##} m");
        Console.WriteLine($"Align Time:   {d.AlignTime:0.####} s");
        Console.WriteLine($"EHP Total:    {d.Ehp:0}  (S {d.ShieldEhp:0} / A {d.ArmorEhp:0} / H {d.StructureEhp:0})");
        var state = d.CapacitorStable ? $"{d.CapacitorStablePercent:0.##}%" : $"{d.CapacitorDepletesInSeconds:0.##}s";
        Console.WriteLine($"Cap:          {d.CapacitorCapacity:0.##} GJ | recharge {d.CapacitorRecharge:0.##} | used {d.CapacitorUsed:0.##} | stable {d.CapacitorStable} | state {state}");
        Console.WriteLine($"Weapon DPS:   {d.TurretDps + d.MissileDps:0.##} | Drone DPS: {d.DroneDps:0.##} | Fighter DPS: {d.FighterDps:0.##} (sustained {d.FighterDpsSustained:0.##}) | Total DPS: {d.TotalDps:0.##}");
        Console.WriteLine($"Drones:       {d.DroneActiveCount} active | bandwidth {d.DroneBandwidthUsed:0.##} / {result.ShipAttribute(1271):0.##}");
        Console.WriteLine($"Fighters:     {fighters.Count} squadrons launched");
        return 0;
    }

    // Fighter squadrons: any category-87 item (wherever the parser put it), grouped per type into ceil(total / squadron
    // size) squadrons, each launched at full strength. Mirrors the fit-detail Fighter Bay seeding. NB the headless check
    // launches every squadron (no per-kind tube cap — that is the UI's FighterBayViewModel); keep validation fits within
    // the hull's tube limits so this matches what the reference launches.
    private static List<FighterInput> BuildFighters(EsiFitting fit, ISdeAccessor sde)
    {
        var accessor = new FighterAccessor(sde);
        var fighters = new List<FighterInput>();
        var byType = fit.Items
            .Select(item => (Type: accessor.GetFighterType(item.TypeId), item.Quantity))
            .Where(entry => entry.Type is not null)
            .GroupBy(entry => entry.Type!.TypeId)
            .Select(group => (Type: group.First().Type!, Fighters: group.Sum(entry => entry.Quantity)));
        foreach (var (type, fighterCount) in byType)
        {
            var squadrons = type.SquadronMaxSize > 0 ? (int)Math.Ceiling((double)fighterCount / type.SquadronMaxSize) : fighterCount;
            for (var index = 0; index < squadrons; index++)
                fighters.Add(new FighterInput(type.TypeId, type.SquadronMaxSize));
        }
        return fighters;
    }

    private static (List<ModuleInput>, List<DroneInput>, List<ImplantInput>) MapFit(
        EsiFitting fit, ISdeAccessor sde, EveUtils.Shared.Modules.Sde.IDogmaDataAccessor data)
    {
        var modules = new List<ModuleInput>();
        var drones = new List<DroneInput>();
        var implants = new List<ImplantInput>();

        // Slot-flagged items in fit order (so the max-active clamp keeps the first module of a group active, as EVE does),
        // one module per slot flag with an optional charge sharing the flag.
        var accumulator = new ModuleStateAccumulator();
        var seenFlags = new HashSet<string>();
        foreach (var entry in fit.Items.Where(item => item.Flag != "DroneBay" && item.Flag != "Cargo"))
        {
            if (!seenFlags.Add(entry.Flag))
                continue;
            var slot = fit.Items.Where(item => item.Flag == entry.Flag).ToList();
            var module = slot.FirstOrDefault(item => sde.GetSlotType(item.TypeId) != SdeSlotType.None);
            if (module is null)
                continue;
            // The reference drops a module whose canFitShipGroup/Type restriction excludes this hull (e.g. a Supercarrier-only
            // Networked Sensor Array on a Carrier). The harness must too, or it over-counts that module's CPU/PG/cap.
            if (!CanFitShip(module.TypeId, fit.ShipTypeId, data))
                continue;
            var charge = slot.FirstOrDefault(item => sde.GetSlotType(item.TypeId) == SdeSlotType.None);

            // Default activation state (active, clamped to online for online-only effects or a reached max-active group
            // limit) — shared with the fit-detail window so both default a fit to the same in-game states.
            var state = ModuleStateResolver.DefaultState(module.TypeId, data, accumulator);

            modules.Add(new ModuleInput(module.TypeId, state, charge?.TypeId));
        }

        foreach (var item in fit.Items.Where(item => item.Flag == "DroneBay"))
            drones.Add(new DroneInput(item.TypeId, item.Quantity));

        // Implants are dumped into cargo by the parser (no slot); pull them back out by category.
        foreach (var item in fit.Items.Where(item => item.Flag == "Cargo" && CategoryOf(item.TypeId, sde) == ImplantCategoryId))
            implants.Add(new ImplantInput(item.TypeId));

        return (modules, drones, implants);
    }

    // canFitShipGroupNN (20 attrs) and canFitShipTypeNN (12 attrs): when present, the module only fits the listed ship
    // groups/types. A hull that matches none cannot mount it (CCP fitting restriction; enforced at import).
    private static readonly int[] CanFitShipGroupAttributes =
        [1298, 1299, 1300, 1301, 1872, 1879, 1880, 1881, 2065, 2396, 2476, 2477, 2478, 2479, 2480, 2481, 2482, 2483, 2484, 2485];

    private static readonly int[] CanFitShipTypeAttributes =
        [1302, 1303, 1304, 1305, 1944, 2103, 2463, 2486, 2487, 2488, 2758, 5948];

    private static bool CanFitShip(int moduleTypeId, int shipTypeId, EveUtils.Shared.Modules.Sde.IDogmaDataAccessor data)
    {
        var attributes = data.GetBaseAttributes(moduleTypeId);
        var allowedTypes = attributes.Where(a => CanFitShipTypeAttributes.Contains(a.AttributeId)).Select(a => (int)a.Value).ToList();
        var allowedGroups = attributes.Where(a => CanFitShipGroupAttributes.Contains(a.AttributeId)).Select(a => (int)a.Value).ToList();
        if (allowedTypes.Count == 0 && allowedGroups.Count == 0)
            return true;   // unrestricted
        return allowedTypes.Contains(shipTypeId) || allowedGroups.Contains(data.GetGroupId(shipTypeId) ?? 0);
    }

    private static int? CategoryOf(int typeId, ISdeAccessor sde)
    {
        var type = sde.GetType(typeId);
        return type is null ? null : sde.GetGroup(type.GroupId)?.CategoryId;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
