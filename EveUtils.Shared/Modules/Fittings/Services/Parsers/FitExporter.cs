using System.Text;
using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>
/// Default <see cref="IFitExporter"/>. EFT export groups items by slot in the conventional order
/// (low → mid → high → rig → subsystem → service, then drones, then cargo), pairs a loaded charge back onto
/// its module line, and resolves typeIds to names via the SDE. DNA export aggregates items by typeId.
/// </summary>
internal sealed class FitExporter(ISdeAccessor sde) : IFitExporter, ISingletonService
{
    private static readonly string[] SlotOrder =
        ["LoSlot", "MedSlot", "HiSlot", "RigSlot", "SubSystemSlot", "ServiceSlot"];

    public string ToEft(EsiFitting fit)
    {
        var blocks = new List<string>();

        foreach (var prefix in SlotOrder)
        {
            var groups = fit.Items
                .Where(i => SlotIndex(i.Flag, prefix) is not null)
                .GroupBy(i => SlotIndex(i.Flag, prefix)!.Value)
                .OrderBy(g => g.Key)
                .ToList();
            if (groups.Count == 0)
                continue;

            var lines = new List<string>();
            foreach (var group in groups)
            {
                var module = group.FirstOrDefault(i => sde.GetSlotType(i.TypeId) != SdeSlotType.None) ?? group.First();
                var charge = group.FirstOrDefault(i => i.TypeId != module.TypeId);
                lines.Add(charge is null
                    ? Name(module.TypeId)
                    : $"{Name(module.TypeId)}, {Name(charge.TypeId)}");
            }
            blocks.Add(string.Join('\n', lines));
        }

        AppendStack(blocks, fit, "DroneBay");
        AppendStack(blocks, fit, "Cargo");

        var shipName = sde.TryGetTypeName(fit.ShipTypeId, out var name) ? name : fit.ShipTypeId.ToString();
        var header = $"[{shipName}, {fit.Name}]";
        return blocks.Count == 0 ? header : $"{header}\n{string.Join("\n\n", blocks)}";
    }

    public string ToDna(EsiFitting fit)
    {
        var builder = new StringBuilder();
        builder.Append(fit.ShipTypeId).Append(':');
        foreach (var group in fit.Items.GroupBy(i => i.TypeId))
            builder.Append(group.Key).Append(';').Append(group.Sum(i => i.Quantity)).Append(':');
        builder.Append(':');
        return builder.ToString();
    }

    public string ToEveshipUrl(EsiFitting fit)
    {
        var sb = new StringBuilder();
        sb.Append("ship,").Append(fit.ShipTypeId).Append(',')
          .Append(Sanitize(fit.Name)).Append(',').Append(Sanitize(fit.Description)).Append('\n');

        foreach (var prefix in SlotOrder)
        {
            var slotType = EveshipSlots.SlotType(prefix);
            if (slotType is null)
                continue; // ServiceSlot has no eveship v3 equivalent

            var groups = fit.Items
                .Where(i => SlotIndex(i.Flag, prefix) is not null)
                .GroupBy(i => SlotIndex(i.Flag, prefix)!.Value)
                .OrderBy(g => g.Key);
            foreach (var group in groups)
            {
                var module = group.FirstOrDefault(i => sde.GetSlotType(i.TypeId) != SdeSlotType.None) ?? group.First();
                var charge = group.FirstOrDefault(i => i.TypeId != module.TypeId);
                // State is unknown in our model (ESI/EFT/DNA carry none) → default to the neutral fitted "Online".
                // eveship.fit v3 numbers slots 1-based, our flags are 0-based (HiSlot0) → emit group.Key + 1.
                sb.Append("module,").Append(slotType).Append(',').Append(group.Key + 1).Append(',')
                  .Append(module.TypeId).Append(",Online,").Append(charge?.TypeId.ToString() ?? string.Empty).Append('\n');
            }
        }

        foreach (var drone in fit.Items.Where(i => i.Flag == "DroneBay"))
            sb.Append("drone,").Append(drone.TypeId).Append(',').Append(drone.Quantity).Append(",0\n");
        foreach (var cargo in fit.Items.Where(i => i.Flag == "Cargo"))
            sb.Append("cargo,").Append(cargo.TypeId).Append(',').Append(cargo.Quantity).Append('\n');

        return EveshipFitCodec.BuildUrl("v3:" + EveshipFitCodec.Compress(sb.ToString()));
    }

    // The v3 CSV is comma-delimited with no escaping, so strip separators from free-text fields (mirrors eveship).
    private static string Sanitize(string value) => value.Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private void AppendStack(List<string> blocks, EsiFitting fit, string flag)
    {
        var lines = fit.Items
            .Where(i => i.Flag == flag)
            .Select(i => i.Quantity > 1 ? $"{Name(i.TypeId)} x{i.Quantity}" : Name(i.TypeId))
            .ToList();
        if (lines.Count > 0)
            blocks.Add(string.Join('\n', lines));
    }

    private string Name(int typeId) => sde.TryGetTypeName(typeId, out var name) ? name : typeId.ToString();

    // Returns the slot index for a flag matching "<prefix><digits>" (e.g. HiSlot2 -> 2), or null otherwise.
    private static int? SlotIndex(string flag, string prefix)
    {
        if (!flag.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var rest = flag.AsSpan(prefix.Length);
        return rest.Length > 0 && int.TryParse(rest, out var index) ? index : null;
    }
}
