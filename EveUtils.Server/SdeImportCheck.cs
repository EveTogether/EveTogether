using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Import;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server;

/// <summary>
/// Headless end-to-end proof of the SDE pipeline (<c>--sde-import</c>): runs a real CCP download + JSONL→SQLite
/// build (idempotent — skips when already on the latest build), then asserts the read-only accessor resolves
/// names↔typeIds case-insensitively, walks type→group→category, returns dogma attributes, and reports the
/// pre-computed slot/hardpoint metadata for a set of canonical modules (verified against build 3374020).
/// Exit 0 = pass, 1 = fail.
/// </summary>
public static class SdeImportCheck
{
    private sealed record SlotExpectation(string Name, int TypeId, SdeSlotType Slot, bool IsTurret);

    private static readonly SlotExpectation[] Modules =
    [
        new("Damage Control II", 2048, SdeSlotType.Low, false),
        new("1MN Afterburner II", 438, SdeSlotType.Medium, false),
        new("200mm AutoCannon II", 2889, SdeSlotType.High, true),
        new("Small Projectile Collision Accelerator I", 31680, SdeSlotType.Rig, false)
    ];

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE Together SDE import + accessor check (real download) ==");

        var importer = services.GetRequiredService<ISdeImporter>();
        var progress = new SyncConsoleProgress();
        var result = await importer.EnsureUpToDateAsync(progress);
        Console.WriteLine();

        var ok = true;
        ok &= Check(result.Success, $"import succeeded ({(result.Updated ? "imported" : "already up to date")}, build {result.Version?.BuildNumber})");
        if (!result.Success)
        {
            Console.WriteLine($"FAIL: {result.Error}");
            return 1;
        }

        var sde = services.GetRequiredService<ISdeAccessor>();
        ok &= Check(sde.IsAvailable, "store is available");
        ok &= Check(sde.Version?.BuildNumber == result.Version?.BuildNumber,
            $"accessor build {sde.Version?.BuildNumber} matches imported build");

        // name <-> typeId, case-insensitive + trimmed.
        ok &= Check(sde.TryGetTypeName(587, out var rifter) && rifter == "Rifter", "typeId 587 -> 'Rifter'");
        ok &= Check(sde.TryGetTypeId("rifter", out var lower) && lower == 587, "'rifter' (lowercase) -> 587");
        ok &= Check(sde.TryGetTypeId("  RIFTER  ", out var trimmed) && trimmed == 587, "'  RIFTER  ' (trim+upper) -> 587");

        // type -> group -> category walk.
        var type = sde.GetType(587);
        var group = type is null ? null : sde.GetGroup(type.GroupId);
        var category = group is null ? null : sde.GetCategory(group.CategoryId);
        ok &= Check(category?.Name == "Ship", $"Rifter category resolves to 'Ship' (got '{category?.Name}')");
        ok &= Check(sde.GetDogmaAttributes(587).Count > 0, "Rifter has dogma attributes");

        // Pre-computed slot/hardpoint metadata for canonical modules.
        foreach (var module in Modules)
        {
            ok &= Check(sde.TryGetTypeId(module.Name, out var id) && id == module.TypeId,
                $"'{module.Name}' -> {module.TypeId}");
            var requirement = sde.GetFitRequirement(module.TypeId);
            ok &= Check(requirement?.SlotType == module.Slot,
                $"'{module.Name}' slot = {module.Slot} (got {requirement?.SlotType.ToString() ?? "null"})");
            ok &= Check(requirement?.IsTurret == module.IsTurret,
                $"'{module.Name}' isTurret = {module.IsTurret}");
        }

        // A ship is not a fittable module: no slot requirement row.
        ok &= Check(sde.GetSlotType(587) == SdeSlotType.None, "Rifter (a ship) has no fitting slot");

        // EFT-smoke: every module name in a paste resolves to a typeId in a real slot.
        var eft = new[] { "Damage Control II", "1MN Afterburner II", "200mm AutoCannon II", "Small Projectile Collision Accelerator I" };
        var eftOk = eft.All(n => sde.TryGetTypeId(n, out var id) && sde.GetSlotType(id) != SdeSlotType.None);
        ok &= Check(eftOk, "EFT-smoke: all module names resolve to a slotted type");

        Console.WriteLine(ok ? "\nPASS" : "\nFAIL");
        return ok ? 0 : 1;
    }

    private static bool Check(bool condition, string label)
    {
        Console.WriteLine($"  [{(condition ? "OK" : "XX")}] {label}");
        return condition;
    }

    /// <summary>Console progress sink: prints download% then "x / y processed" at ~10% steps (no flooding).</summary>
    private sealed class SyncConsoleProgress : IProgress<SdeImportProgress>
    {
        private int _lastBucket = -1;
        private SdeImportPhase _lastPhase = (SdeImportPhase)(-1);

        public void Report(SdeImportProgress value)
        {
            if (value.Phase != _lastPhase)
            {
                _lastBucket = -1;
                _lastPhase = value.Phase;
                Console.WriteLine($"\n  -> {value.Phase}");
            }

            switch (value.Phase)
            {
                case SdeImportPhase.Downloading when value.TotalBytes > 0:
                    PrintBucket((int)(value.DownloadFraction * 100), $"download {value.DownloadedBytes / 1_048_576} / {value.TotalBytes / 1_048_576} MB");
                    break;
                case SdeImportPhase.Processing when value.TotalItems > 0:
                    PrintBucket((int)(value.ProcessFraction * 100), $"processed {value.ProcessedItems:n0} / {value.TotalItems:n0}");
                    break;
            }
        }

        private void PrintBucket(int percent, string text)
        {
            var bucket = percent / 10;
            if (bucket == _lastBucket)
                return;
            _lastBucket = bucket;
            Console.WriteLine($"     {percent,3}%  {text}");
        }
    }
}
