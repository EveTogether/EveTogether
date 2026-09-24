using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Shared.Modules.Killmails;

/// <summary>
/// Reconstructs a read-only <see cref="EsiFitting"/> from a killmail's items (ET-333) — what was fitted, not what
/// survived. A loaded charge keeps the module's own flag, exactly as ESI's own fittings do; drones, fighters and
/// cargo are one row per type (already summed per (Flag, TypeId) by the importer). Nested items, implants/boosters
/// and unknown flags are left out — a pod mail's implants/boosters come from the character in FIT DETAIL, not from
/// the fit, and a nested item was never fitted to a slot at all.
/// </summary>
public static class KillmailFitBuilder
{
    public static EsiFitting Build(LocalKillmail killmail, string name)
    {
        List<EsiFittingItem> items = [];
        foreach (LocalKillmailItem item in killmail.Items)
        {
            if (item.IsNested)
            {
                continue;
            }

            string? flag = KillmailFlags.NameOf(item.Flag);
            if (flag is null or "Implant" or "Booster")
            {
                continue;
            }

            long quantity = item.QuantityDestroyed + item.QuantityDropped;
            if (quantity <= 0)
            {
                continue;
            }

            items.Add(new EsiFittingItem(item.TypeId, flag, (int)quantity));
        }

        // FittingId only ever serves FitDetailWindowViewModel.ModuleId here (this fit is never stored) — the
        // killmail id keeps two different killmails' FIT DETAIL tabs apart instead of colliding on a shared 0.
        return new EsiFitting(killmail.KillmailId, name, string.Empty, killmail.VictimShipTypeId, items);
    }
}
