using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Tally;

/// <summary>What a run's captures actually amount to. The open window and the stored run both count here, so a run
/// cannot total one way while it is running and another way once it is saved.</summary>
public static class LootTally
{
    /// <summary>Without a cargo hold to start from, every capture is loot — the reading a pilot who never pastes a
    /// starting hold keeps. With one, the loot is the difference between two holds and the moments in between count
    /// towards nothing, because that difference already covers them.</summary>
    public static IReadOnlyList<LootTallyLine> Count(IReadOnlyList<LootTallyCapture> captures)
    {
        // FindLast, both times: two captures carrying the same role is a state the picker cannot produce but the
        // model allows — a synced run, or a second way in built later — and the latest one wins, which is the same
        // answer a capture arriving late gets everywhere else here. Never the reading order's accident.
        List<LootTallyCapture> counted = [.. captures.Where(capture => !capture.IsExcluded)];
        int before = counted.FindLastIndex(capture => capture.Role == LootCaptureRole.CargoBefore);
        if (before < 0)
            return [.. counted.SelectMany(capture => capture.Lines)];

        // No hold named as the end of the run: the last capture is it, which is what makes a capture arriving late
        // win without the pilot having to say so again.
        int after = counted.FindLastIndex(capture => capture.Role == LootCaptureRole.CargoAfter);
        if (after <= before)
            after = counted.Count - 1;

        // A starting hold with nothing after it is a run that has not been counted yet, not a run that lost its
        // whole cargo.
        return after <= before ? [] : _Difference(counted[before], counted[after]);
    }

    private static IReadOnlyList<LootTallyLine> _Difference(LootTallyCapture before, LootTallyCapture after)
    {
        Dictionary<int, long> quantities = [];
        foreach (LootTallyLine line in after.Lines)
            quantities[line.ItemTypeId] = quantities.GetValueOrDefault(line.ItemTypeId) + (line.Quantity ?? 1);
        foreach (LootTallyLine line in before.Lines)
            quantities[line.ItemTypeId] = quantities.GetValueOrDefault(line.ItemTypeId) - (line.Quantity ?? 1);

        // What went up during the run is spent, never loot with a minus in front of it (LootKind.Lost).
        return
        [
            .. quantities
                .Where(item => item.Value != 0)
                .Select(item => new LootTallyLine(
                    item.Key,
                    Math.Abs(item.Value),
                    _UnitVolume([.. after.Lines, .. before.Lines], item.Key) * Math.Abs(item.Value),
                    item.Value > 0 ? LootKind.Gained : LootKind.Lost))
        ];
    }

    /// <summary>An EVE inventory's volume column is the whole stack's, so a difference of three of them is worth
    /// three units of it and not the stack it was cut from.</summary>
    private static decimal? _UnitVolume(IReadOnlyList<LootTallyLine> lines, int itemTypeId) =>
        lines.FirstOrDefault(line => line.ItemTypeId == itemTypeId && line.Volume is not null && line.Quantity > 0)
            is { Volume: { } volume, Quantity: { } quantity }
            ? volume / quantity
            : null;
}
