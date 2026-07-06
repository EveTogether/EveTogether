using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>
/// Default <see cref="IFitTextImporter"/>. Auto-detects EFT vs DNA, parses with the pure parser, then assembles an
/// <see cref="EsiFitting"/> against the SDE: the ship resolves to a hull typeId, each item to a typeId, and the
/// slot flag comes from the SDE's pre-computed slot table (so no dogma is needed at parse time). Unresolved item
/// names are reported as non-fatal warnings; a missing ship or an unloaded SDE is a hard failure.
/// </summary>
public sealed partial class FitTextImporter(ISdeAccessor sde) : IFitTextImporter, ISingletonService
{
    private const int DroneCategoryId = 18;

    [GeneratedRegex(@"^\d+:\d")]
    private static partial Regex DnaShape();

    public FitTextFormat Detect(string text)
    {
        var trimmed = text.TrimStart();
        if (EveshipFitCodec.IsEveshipInput(trimmed))
            return FitTextFormat.Eveship;
        if (trimmed.StartsWith('['))
            return FitTextFormat.Eft;
        return DnaShape().IsMatch(trimmed) ? FitTextFormat.Dna : FitTextFormat.Unknown;
    }

    public FitImportResult Import(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return FitImportResult.Failed("Nothing to import — paste an EFT block or a DNA string.");

        var format = Detect(text);
        if (format == FitTextFormat.Eveship)
            return ImportEveship(text);

        var raw = format switch
        {
            FitTextFormat.Eft => EftFitParser.Parse(text),
            FitTextFormat.Dna => DnaFitParser.Parse(text),
            _ => null
        };
        if (raw is null)
            return FitImportResult.Failed("Unrecognised fit format. Paste an EFT block ([Ship, name] …), a DNA string or an eveship.fit link.");

        if (!sde.IsAvailable)
            return FitImportResult.Failed("EVE static data is not loaded yet — download it from Settings first.");

        return Assemble(raw);
    }

    private FitImportResult ImportEveship(string text)
    {
        if (!EveshipFitCodec.TryExtractPayload(text, out var type, out var data))
            return FitImportResult.Failed("Not a recognised eveship.fit link.");

        string payload;
        try
        {
            payload = EveshipFitCodec.Decompress(data);
        }
        catch (Exception ex)
        {
            return FitImportResult.Failed($"Could not decode the eveship.fit link ({ex.Message}).");
        }

        return type switch
        {
            "v3" => ParseEveshipV3(payload),
            "eft" => Import(payload), // the eft payload is a plain EFT block → reuse the name-resolving EFT path
            "v1" or "v2" => FitImportResult.Failed("Old eveship.fit links (v1/v2) aren't supported yet — open it on eveship.fit and copy a fresh link."),
            "killmail" => FitImportResult.Failed("Killmail eveship.fit links aren't supported yet."),
            _ => FitImportResult.Failed($"Unsupported eveship.fit type '{type}'.")
        };
    }

    // v3 carries explicit typeIds + slot types, so it maps straight to the internal model with no SDE lookup.
    private static FitImportResult ParseEveshipV3(string csv)
    {
        var shipTypeId = 0;
        var name = "Imported fit";
        var description = string.Empty;
        var items = new List<EsiFittingItem>();
        var warnings = new List<string>();

        foreach (var rawLine in csv.Replace("\r", "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            var f = line.Split(',');
            switch (f[0])
            {
                case "ship" when f.Length >= 2 && int.TryParse(f[1], out var shipId):
                    shipTypeId = shipId;
                    if (f.Length >= 3 && f[2].Length > 0)
                        name = f[2];
                    description = f.Length >= 4 ? string.Join(",", f[3..]) : string.Empty;
                    break;
                case "module" when f.Length >= 4
                    && EveshipSlots.FlagPrefix(f[1]) is { } prefix
                    && int.TryParse(f[2], out var slotIndex)
                    && int.TryParse(f[3], out var moduleTypeId):
                    // eveship.fit v3 numbers slots 1-based (High,1 = the first high slot); ESI fitting flags are
                    // 0-based (HiSlot0). Without this shift every module lands one slot too high (live link 2026-06-10).
                    var flag = prefix + Math.Max(0, slotIndex - 1);
                    items.Add(new EsiFittingItem(moduleTypeId, flag, 1));
                    if (f.Length >= 6 && int.TryParse(f[5], out var chargeTypeId) && chargeTypeId > 0)
                        items.Add(new EsiFittingItem(chargeTypeId, flag, 1));
                    break;
                case "drone" when f.Length >= 3 && int.TryParse(f[1], out var droneTypeId):
                    var active = f.Length >= 3 && int.TryParse(f[2], out var a) ? a : 0;
                    var passive = f.Length >= 4 && int.TryParse(f[3], out var p) ? p : 0;
                    if (active + passive > 0)
                        items.Add(new EsiFittingItem(droneTypeId, "DroneBay", active + passive));
                    break;
                case "cargo" when f.Length >= 3 && int.TryParse(f[1], out var cargoTypeId) && int.TryParse(f[2], out var cargoQty):
                    items.Add(new EsiFittingItem(cargoTypeId, "Cargo", cargoQty));
                    break;
            }
        }

        return shipTypeId == 0
            ? FitImportResult.Failed("eveship.fit link has no ship.")
            : FitImportResult.Ok(new EsiFitting(FittingId: 0, name, description, shipTypeId, items), warnings);
    }

    private FitImportResult Assemble(RawFit raw)
    {
        int shipTypeId;
        if (raw.ShipTypeId is { } id)
        {
            if (!sde.TryGetTypeName(id, out _))
                return FitImportResult.Failed($"Unknown ship type id {id}.");
            shipTypeId = id;
        }
        else if (raw.ShipName is not null && sde.TryGetTypeId(raw.ShipName, out shipTypeId))
        {
            // resolved
        }
        else
        {
            return FitImportResult.Failed($"Unknown ship '{raw.ShipName}'.");
        }

        var allocator = new SlotFlagAllocator();
        var items = new List<EsiFittingItem>();
        var warnings = new List<string>();

        // EFT lists the slot racks first, then the drones and cargo sections (where modules kept as cargo — a spare
        // Damage Control, refit plates — also live, each carrying an xN quantity). A module-typed item in such a
        // trailing section must go to the cargo hold, not a slot. The trailing region starts at the first non-empty
        // section that is entirely xN-quantified and has an earlier section before it (a single "Module xN" fit has
        // no earlier section, so its repeated modules are still fitted into slots).
        var trailingFromSection = int.MaxValue;
        var sawEarlierSection = false;
        foreach (var group in raw.Items.GroupBy(item => item.Section).OrderBy(group => group.Key))
        {
            if (sawEarlierSection && group.All(item => item.ExplicitQuantity))
            {
                trailingFromSection = group.Key;
                break;
            }
            sawEarlierSection = true;
        }

        foreach (var entry in raw.Items)
        {
            int typeId;
            if (entry.TypeId is { } known)
            {
                if (!sde.TryGetTypeName(known, out _))
                {
                    warnings.Add($"Skipped unknown type id {known}.");
                    continue;
                }
                typeId = known;
            }
            else if (entry.Name is not null && sde.TryGetTypeId(entry.Name, out typeId))
            {
                // resolved by name
            }
            else
            {
                warnings.Add($"Skipped unknown item: {entry.Name}");
                continue;
            }

            var slot = sde.GetSlotType(typeId);
            if (slot != SdeSlotType.None && entry.Section < trailingFromSection)
            {
                var copies = Math.Max(1, entry.Quantity);
                int? chargeId = null;
                if (entry.ChargeName is not null)
                {
                    if (sde.TryGetTypeId(entry.ChargeName, out var resolvedCharge))
                        chargeId = resolvedCharge;
                    else
                        warnings.Add($"Skipped unknown charge: {entry.ChargeName}");
                    copies = 1; // a "module, charge" line is one module loaded with the charge
                }

                for (var i = 0; i < copies; i++)
                {
                    var flag = allocator.Allocate(slot);
                    items.Add(new EsiFittingItem(typeId, flag, 1));
                    if (chargeId is { } charge)
                        items.Add(new EsiFittingItem(charge, flag, 1));
                }
            }
            else
            {
                var flag = IsDrone(typeId) ? "DroneBay" : "Cargo";
                items.Add(new EsiFittingItem(typeId, flag, Math.Max(1, entry.Quantity)));
            }
        }

        var fit = new EsiFitting(FittingId: 0, raw.FitName, Description: string.Empty, shipTypeId, items);
        return FitImportResult.Ok(fit, warnings);
    }

    private bool IsDrone(int typeId)
    {
        var type = sde.GetType(typeId);
        if (type is null)
            return false;
        var group = sde.GetGroup(type.GroupId);
        return group is not null && group.CategoryId == DroneCategoryId;
    }
}
