using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Import;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server;

/// <summary>
/// Headless real-SDE proof of the fit text parsers (<c>--fit-parse-test</c>): ensures the SDE store exists,
/// then parses a canonical EFT block and a DNA string and asserts the ship + every module resolves to the right
/// slot against the live static data (build 3374020). Exit 0 = pass, 1 = fail.
/// </summary>
public static class FitParseCheck
{
    private const string Eft = """
        [Rifter, Real Rifter]
        Damage Control II
        Small Armor Repairer II

        1MN Afterburner II
        Warp Scrambler II
        Stasis Webifier II

        200mm AutoCannon II, EMP S
        200mm AutoCannon II, EMP S
        200mm AutoCannon II, EMP S

        Small Projectile Collision Accelerator I

        Hobgoblin II x5
        EMP S x1000
        """;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE Together fit-parse check (EFT + DNA against the real SDE) ==");

        var importResult = await services.GetRequiredService<ISdeImporter>().EnsureUpToDateAsync();
        if (!importResult.Success)
        {
            Console.WriteLine($"FAIL: SDE not available ({importResult.Error})");
            return 1;
        }

        var sde = services.GetRequiredService<ISdeAccessor>();
        var importer = services.GetRequiredService<IFitTextImporter>();
        int Id(string name) => sde.TryGetTypeId(name, out var id) ? id : -1;
        var ok = true;

        // --- EFT ---
        ok &= Check(importer.Detect(Eft) == FitTextFormat.Eft, "EFT auto-detected");
        var eft = importer.Import(Eft);
        ok &= Check(eft.Success, $"EFT parsed ({eft.Error})");
        if (eft.Success)
        {
            var fit = eft.Fit!;
            ok &= Check(fit.ShipTypeId == 587, $"ship -> Rifter (587), got {fit.ShipTypeId}");
            ok &= Check(FlagOf(fit, Id("Damage Control II")) == "LoSlot0", "Damage Control II -> LoSlot0");
            ok &= Check(PrefixCount(fit, Id("200mm AutoCannon II"), "HiSlot") == 3, "three 200mm AutoCannon II -> 3 high slots");
            ok &= Check(PrefixCount(fit, Id("1MN Afterburner II"), "MedSlot") == 1, "1MN Afterburner II -> a mid slot");
            ok &= Check(PrefixCount(fit, Id("Small Projectile Collision Accelerator I"), "RigSlot") == 1, "rig -> a rig slot");
            ok &= Check(QuantitySum(fit, Id("Hobgoblin II"), "DroneBay") == 5, "Hobgoblin II x5 -> DroneBay qty 5");
            ok &= Check(QuantitySum(fit, Id("EMP S"), "Cargo") == 1000, "EMP S x1000 -> Cargo qty 1000");
            // The loaded charge shares its launcher's high slot (3 loaded launchers -> 3 EMP S in high slots).
            ok &= Check(fit.Items.Count(i => i.TypeId == Id("EMP S") && i.Flag.StartsWith("HiSlot")) == 3,
                "loaded EMP S sits in the launchers' high slots");
            ok &= Check(eft.Warnings.Count == 0, $"no unexpected warnings ({string.Join("; ", eft.Warnings)})");
        }

        // --- DNA ---
        const string dna = "587:2048;1:2889;3::";
        ok &= Check(importer.Detect(dna) == FitTextFormat.Dna, "DNA auto-detected");
        var dnaResult = importer.Import(dna);
        ok &= Check(dnaResult.Success, $"DNA parsed ({dnaResult.Error})");
        if (dnaResult.Success)
        {
            ok &= Check(dnaResult.Fit!.ShipTypeId == 587, "DNA ship -> 587");
            ok &= Check(dnaResult.Fit.Items.Count(i => i.TypeId == 2889) == 3, "DNA 200mm AutoCannon II x3 -> 3 items");
        }

        Console.WriteLine(ok ? "\nPASS" : "\nFAIL");
        return ok ? 0 : 1;
    }

    private static string? FlagOf(EsiFitting fit, int typeId) =>
        fit.Items.FirstOrDefault(i => i.TypeId == typeId)?.Flag;

    private static int PrefixCount(EsiFitting fit, int typeId, string prefix) =>
        fit.Items.Count(i => i.TypeId == typeId && i.Flag.StartsWith(prefix, StringComparison.Ordinal));

    private static int QuantitySum(EsiFitting fit, int typeId, string flag) =>
        fit.Items.Where(i => i.TypeId == typeId && i.Flag == flag).Sum(i => i.Quantity);

    private static bool Check(bool condition, string label)
    {
        Console.WriteLine($"  [{(condition ? "OK" : "XX")}] {label}");
        return condition;
    }
}
