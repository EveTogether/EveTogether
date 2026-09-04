using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Tally;

/// <summary>What a run's captures actually amount to. The open window and the stored run both count here, so a run
/// cannot total one way while it is running and another way once it is saved.</summary>
public static class LootTally
{
    /// <summary>Which two captures the difference runs between, as indexes into <paramref name="captures"/>.
    /// <c>Before</c> is -1 when no starting hold is named and every capture simply counts; <c>After</c> is -1 when a
    /// starting hold has nothing after it yet. The window names these two on screen and <see cref="Count"/> counts
    /// between them — one answer, so the caption cannot say one thing while the figures say another.</summary>
    public static (int Before, int After) Ends(IReadOnlyList<LootTallyCapture> captures)
    {
        int before = _LastWithRole(captures, LootCaptureRole.CargoBefore);
        if (before < 0)
            return (-1, -1);

        // A named ending hold is the ending hold wherever it sits in the run: pasting the two boxes in the other
        // order must not leave the run uncounted. Only when none is named does the last capture become it — which is
        // what makes a capture arriving late win without the pilot having to say so again.
        int after = _LastWithRole(captures, LootCaptureRole.CargoAfter);
        return (before, after >= 0 ? after : _LastCountedAfter(captures, before));
    }

    /// <summary>Without a starting hold, every capture is loot — the reading a pilot who never pastes one keeps.
    /// With one, the loot is the difference between two holds and the moments in between count towards nothing,
    /// because that difference already covers them.</summary>
    public static IReadOnlyList<LootTallyLine> Count(IReadOnlyList<LootTallyCapture> captures)
    {
        (int before, int after) = Ends(captures);
        if (before < 0)
            return [.. captures.Where(capture => !capture.IsExcluded).SelectMany(capture => capture.Lines)];

        // A starting hold with nothing after it is a run that has not been counted yet, not a run that lost its
        // whole cargo.
        return after < 0 ? [] : _Difference(captures[before], captures[after]);
    }

    /// <summary>Two captures carrying the same role is a state the picker cannot produce but the model allows — a
    /// synced run, or a second way in built later — and the latest one wins, which is the answer a capture arriving
    /// late gets everywhere else here. Never the reading order's accident.</summary>
    private static int _LastWithRole(IReadOnlyList<LootTallyCapture> captures, LootCaptureRole role)
    {
        for (int index = captures.Count - 1; index >= 0; index--)
            if (!captures[index].IsExcluded && captures[index].Role == role)
                return index;

        return -1;
    }

    private static int _LastCountedAfter(IReadOnlyList<LootTallyCapture> captures, int before)
    {
        for (int index = captures.Count - 1; index > before; index--)
            if (!captures[index].IsExcluded)
                return index;

        return -1;
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
