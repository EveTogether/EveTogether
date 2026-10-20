using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>
/// What can drop in one activity, out of its samples (ET-519) — pure, no IO, the same for the own runs and a server's
/// outcomes. A drop rate counts only runs whose loot was counted between a starting and an ending hold: a run that was
/// never pasted is not "a run with nothing" and would pull every rate down. A fleet run holds one pilot's loot, not the
/// activity's, and is left out and reported (Jithran 18-10). Each pilot weighs at most
/// <see cref="LootTablePolicy.RunsPerPilotCap"/> runs, as in <see cref="EarningsStatistics"/>.
/// </summary>
public static class LootTableStatistics
{
    private sealed record Counted(EarningsSample Sample, double Weight, Dictionary<int, long> Quantities);

    /// <param name="classify">The catalogue class of a type; <see cref="LootRarityClass.Common"/> when the catalogue is missing.</param>
    /// <param name="unitPrice">Today's price per type; null counts the item as 0 and reports it unpriced.</param>
    /// <param name="updateUtc">The last game update, or null: then <see cref="LootTableWindow.SinceUpdate"/> reads the long window and nothing is flagged.</param>
    public static LootTableDto Compute(ActivityStatsKey key, IReadOnlyList<EarningsSample> samples, Func<int, LootRarityClass> classify,
        Func<int, decimal?> unitPrice, LootTablePolicy policy, LootTableWindow window, DateTime nowUtc, DateTime? updateUtc)
    {
        DateTime sinceUtc = nowUtc - _SpanOf(policy, window, nowUtc, updateUtc);
        IReadOnlyList<Counted> counted = _Weighted(_Counted(samples, sinceUtc, policy), policy);
        EarningsSample[] inWindow = [.. samples.Where(sample => sample.CompletedAtUtc >= sinceUtc && _Weighs(sample, policy))];
        double effective = counted.Sum(entry => entry.Weight);
        LootTableConfidence confidence = effective >= policy.RatedRuns ? LootTableConfidence.Rated
            : effective >= policy.MinimumRuns ? LootTableConfidence.Seen
            : LootTableConfidence.None;
        int pilots = counted.Select(entry => entry.Sample.PilotBucket).Distinct().Count();
        int fleetRuns = inWindow.Count(_IsFleet);
        int uncounted = inWindow.Count(sample => !_IsFleet(sample) && !_IsComplete(sample));
        if (confidence is LootTableConfidence.None)
            return _Names(key, counted, classify, window, sinceUtc, updateUtc, pilots, fleetRuns, uncounted, effective);

        Dictionary<int, LootRarityClass> classes = [];
        LootRarityClass ClassOf(int typeId) =>
            classes.TryGetValue(typeId, out LootRarityClass known) ? known : classes[typeId] = classify(typeId);

        Dictionary<int, decimal?> prices = [];
        decimal? PriceOf(int typeId) =>
            prices.TryGetValue(typeId, out decimal? known) ? known : prices[typeId] = unitPrice(typeId);

        int[] typeIds = [.. counted.SelectMany(entry => entry.Quantities.Keys).Distinct()];
        Dictionary<int, LootItemChange> changes = window is LootTableWindow.SinceUpdate || updateUtc is not { } update
            ? []
            : _Changes(samples, typeIds, policy, nowUtc, update);
        (decimal Value, double Weight)[] values = [.. counted
            .Select(entry => (Value: _LootValue(entry.Quantities, PriceOf), entry.Weight))
            .OrderBy(entry => entry.Value)];
        decimal expected = values.Sum(entry => entry.Value * (decimal)entry.Weight) / (decimal)effective;
        LootTableItemDto[] items = [.. typeIds
            .Select(typeId => _Item(typeId, counted, effective, expected, confidence, policy, ClassOf(typeId), PriceOf(typeId),
                changes.GetValueOrDefault(typeId)))
            .OrderByDescending(item => item.Contribution)
            .ThenByDescending(item => item.DropRate)
            .ThenBy(item => item.TypeId)];

        LootTableItemDto[] rare = [.. items.Where(item => item.IsRare)];
        HashSet<int> rareTypes = [.. rare.Select(item => item.TypeId)];
        Counted[] withRare = [.. counted.Where(entry => entry.Quantities.Keys.Any(rareTypes.Contains))];
        return new LootTableDto
        {
            Key = key,
            Confidence = confidence,
            Window = window,
            SinceUtc = sinceUtc,
            UpdateUtc = updateUtc,
            RunCount = counted.Count,
            EffectiveRunCount = effective,
            PilotCount = pilots,
            UncountedRunCount = uncounted,
            FleetRunCount = fleetRuns,
            UnpricedItemCount = items.Count(item => item.UnitPrice is null),
            ExpectedLoot = expected,
            TypicalLoot = WeightedQuantile.Of(values, 0.5),
            ExpectedFixedIsk = counted.Sum(entry => entry.Sample.FixedIsk * (decimal)entry.Weight) / (decimal)effective,
            RareKindCount = rare.Length,
            RareRunCount = withRare.Length,
            RareRunShare = withRare.Sum(entry => entry.Weight) / effective,
            RareValueShare = rare.Sum(item => item.ValueShare),
            HasUpdateFlags = changes.Count > 0,
            Items = items
        };
    }

    /// <summary>How many runs the table of this activity would count over its long window: the ones whose loot was counted
    /// between a starting and an ending hold, fleet runs left out (ET-521). What the picker says a site has to go on.</summary>
    public static int CountedRunCount(IEnumerable<EarningsSample> samples, LootTablePolicy policy, DateTime nowUtc) =>
        _Counted(samples, nowUtc - policy.LongWindow, policy).Count();

    /// <summary>The 90 % Wilson score interval of <paramref name="successes"/> out of <paramref name="trials"/>, which
    /// may be weighted counts: it stays inside 0-1 and does not collapse for a rate of 0 or 1 the way the normal one does.</summary>
    public static (double Low, double High) WilsonInterval(double successes, double trials, double z)
    {
        if (trials <= 0)
            return (0, 1);

        double rate = successes / trials;
        double zSquared = z * z;
        double denominator = 1 + zSquared / trials;
        double centre = (rate + zSquared / (2 * trials)) / denominator;
        double margin = z * Math.Sqrt(rate * (1 - rate) / trials + zSquared / (4 * trials * trials)) / denominator;
        return (Math.Max(0, centre - margin), Math.Min(1, centre + margin));
    }

    private static TimeSpan _SpanOf(LootTablePolicy policy, LootTableWindow window, DateTime nowUtc, DateTime? updateUtc) =>
        window switch
        {
            LootTableWindow.Days30 => policy.ShortWindow,
            LootTableWindow.SinceUpdate when updateUtc is { } update => _Clamp(nowUtc - update, policy.LongWindow),
            _ => policy.LongWindow
        };

    private static TimeSpan _Clamp(TimeSpan span, TimeSpan longest) => span < longest ? span : longest;

    // A server's weight 0 is a run its own statistic leaves out (too short, no counted loot): not ours to report either.
    private static bool _Weighs(EarningsSample sample, LootTablePolicy policy) =>
        sample.CapWeight is not 0
        && !(sample.DurationSeconds is { } seconds && seconds < policy.MinimumDuration.TotalSeconds);

    private static bool _IsFleet(EarningsSample sample) => sample.FleetSize > 1;

    private static bool _IsComplete(EarningsSample sample) =>
        sample.LootBasis is LootBasis.CargoDifference && sample.OpenUnrecognisedLootCount == 0;

    private static IEnumerable<EarningsSample> _Counted(IEnumerable<EarningsSample> samples, DateTime sinceUtc,
        LootTablePolicy policy) =>
        samples.Where(sample => sample.CompletedAtUtc >= sinceUtc && _Weighs(sample, policy) && !_IsFleet(sample)
                                && _IsComplete(sample));

    // w = min(1, C / runs of that pilot in the window), unless a server already applied it.
    private static IReadOnlyList<Counted> _Weighted(IEnumerable<EarningsSample> samples, LootTablePolicy policy)
    {
        EarningsSample[] all = [.. samples];
        Dictionary<string, int> runsByPilot = all
            .GroupBy(sample => sample.PilotBucket, StringComparer.Ordinal)
            .ToDictionary(pilot => pilot.Key, pilot => pilot.Count(), StringComparer.Ordinal);
        return [.. all.Select(sample => new Counted(sample,
            sample.CapWeight ?? Math.Min(1d, (double)policy.RunsPerPilotCap / runsByPilot[sample.PilotBucket]),
            _Quantities(sample)))];
    }

    // One run drops a type once however many lines hold it: a local reading merges them already, a wire one may not.
    private static Dictionary<int, long> _Quantities(EarningsSample sample) =>
        sample.LootGained
            .Where(line => line.Quantity > 0)
            .GroupBy(line => line.TypeId)
            .ToDictionary(type => type.Key, type => type.Sum(line => line.Quantity));

    private static LootTableItemDto _Item(int typeId, IReadOnlyList<Counted> counted, double effective, decimal expected,
        LootTableConfidence tableConfidence, LootTablePolicy policy, LootRarityClass catalogueClass, decimal? price,
        LootItemChange change)
    {
        Counted[] seen = [.. counted.Where(entry => entry.Quantities.ContainsKey(typeId))];
        double seenWeight = seen.Sum(entry => entry.Weight);
        double rate = seenWeight / effective;
        // Frequency makes a common item rare only where a rate is trusted at all.
        LootRarityClass rarity = catalogueClass is LootRarityClass.Common && tableConfidence is LootTableConfidence.Rated
                                 && rate < policy.RareDropRate
            ? LootRarityClass.Seldom
            : catalogueClass;
        bool rated = tableConfidence is LootTableConfidence.Rated && seen.Length >= policy.MinimumSightings
                     && rarity is LootRarityClass.Common;
        double average = seen.Sum(entry => entry.Weight * entry.Quantities[typeId]) / seenWeight;
        decimal valuePerDrop = (decimal)average * price.GetValueOrDefault();
        decimal contribution = (decimal)rate * valuePerDrop;
        (double low, double high) = WilsonInterval(seenWeight, effective, policy.IntervalZ);
        return new LootTableItemDto
        {
            TypeId = typeId,
            RarityClass = rarity,
            Confidence = rated ? LootTableConfidence.Rated : LootTableConfidence.Seen,
            RunCount = seen.Length,
            DropRate = rate,
            DropRateLow = low,
            DropRateHigh = high,
            AverageQuantity = average,
            MinimumQuantity = seen.Min(entry => entry.Quantities[typeId]),
            MaximumQuantity = seen.Max(entry => entry.Quantities[typeId]),
            LastSeenUtc = seen.Max(entry => entry.Sample.CompletedAtUtc),
            UnitPrice = price,
            ValuePerDrop = valuePerDrop,
            Contribution = contribution,
            ValueShare = expected > 0 ? (double)(contribution / expected) : 0d,
            Change = change
        };
    }

    private static decimal _LootValue(IReadOnlyDictionary<int, long> quantities, Func<int, decimal?> price) =>
        quantities.Sum(line => price(line.Key).GetValueOrDefault() * line.Value);

    // The runs before and after the update over the long window, whatever the table shows: with a 30-day table the
    // flags are still told from enough runs on both sides or not at all.
    private static Dictionary<int, LootItemChange> _Changes(IReadOnlyList<EarningsSample> samples, IReadOnlyList<int> typeIds,
        LootTablePolicy policy, DateTime nowUtc, DateTime updateUtc)
    {
        EarningsSample[] runs = [.. _Counted(samples, nowUtc - policy.LongWindow, policy)];
        Dictionary<int, long>[] before = [.. runs.Where(run => run.CompletedAtUtc < updateUtc).Select(_Quantities)];
        Dictionary<int, long>[] after = [.. runs.Where(run => run.CompletedAtUtc >= updateUtc).Select(_Quantities)];
        if (before.Length < policy.MinimumRunsPerSideOfUpdate || after.Length < policy.MinimumRunsPerSideOfUpdate)
            return [];

        Dictionary<int, LootItemChange> changes = [];
        foreach (int typeId in typeIds)
        {
            double earlier = before.Count(run => run.ContainsKey(typeId)) / (double)before.Length;
            double later = after.Count(run => run.ContainsKey(typeId)) / (double)after.Length;
            if (earlier >= policy.GoneDropRate && later == 0)
                changes[typeId] = LootItemChange.NotSeenSinceUpdate;
            else if (later >= policy.NewDropRate && earlier == 0)
                changes[typeId] = LootItemChange.NewSinceUpdate;
        }

        return changes;
    }

    // Below the minimum: what was seen, by name only.
    private static LootTableDto _Names(ActivityStatsKey key, IReadOnlyList<Counted> counted, Func<int, LootRarityClass> classify,
        LootTableWindow window, DateTime sinceUtc, DateTime? updateUtc, int pilots, int fleetRuns, int uncounted, double effective) =>
        new()
        {
            Key = key,
            Confidence = LootTableConfidence.None,
            Window = window,
            SinceUtc = sinceUtc,
            UpdateUtc = updateUtc,
            RunCount = counted.Count,
            EffectiveRunCount = effective,
            PilotCount = pilots,
            UncountedRunCount = uncounted,
            FleetRunCount = fleetRuns,
            Items =
            [
                .. counted
                    .SelectMany(entry => entry.Quantities.Keys)
                    .Distinct()
                    .Order()
                    .Select(typeId => new LootTableItemDto
                    {
                        TypeId = typeId,
                        RarityClass = classify(typeId),
                        Confidence = LootTableConfidence.None
                    })
            ]
        };
}
