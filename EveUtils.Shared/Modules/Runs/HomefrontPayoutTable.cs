using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs;

/// <summary>
/// What a homefront pays per character counted in the site, by N — data, never a formula (ET-231). CCP publishes only
/// the optimum; everything else is read off the community's own graphs (domain/homefronts.md §3.2), and CCP has
/// changed the whole table three times (2023, 2024, 19 Mar 2026 — patch 23.02). A change is one more
/// <see cref="_Versions"/> entry, never a rewritten formula, and every run remembers which entry it used
/// (<see cref="Entities.Run.HomefrontPayoutTableVersion"/>) so two clients on two app versions never silently
/// disagree about the same site.
///
/// Only the table CCP shipped on 19 Mar 2026 is in here today. The 2023 and 2024 tables are not: nobody has measured
/// what they actually paid (domain/homefronts.md §1 flags this as unmeasured), and a guessed number is worse than
/// none. A run stopped before 19 Mar 2026 gets no table and no expected payout — never today's table applied to a
/// site it never priced. Add the older tables here, each with its own <c>EffectiveFromUtc</c>, the day someone
/// actually measures them.
/// </summary>
public static class HomefrontPayoutTable
{
    private sealed record Version(
        string Label,
        DateTime EffectiveFromUtc,
        IReadOnlyDictionary<int, decimal> FivePerson,
        IReadOnlyDictionary<int, decimal> ThreePerson,
        IReadOnlyDictionary<int, decimal> Metaliminal,
        IReadOnlyDictionary<int, decimal> AarTotal,
        IReadOnlyDictionary<int, decimal> AarWaveBandAtN5);

    // domain/homefronts.md §3.2, read off the wiki's 2026 payout graphs (patch 23.02, in effect since 19 Mar 2026).
    // N=1 is not in the graph, which starts at N=2 — carried over as documented there, marked [vermoeden].
    private static readonly Version _23_02 = new(
        Label: "2026-03-19",
        EffectiveFromUtc: new DateTime(2026, 3, 19, 0, 0, 0, DateTimeKind.Utc),
        FivePerson: new Dictionary<int, decimal>
        {
            [1] = 6_144_000m, [2] = 7_680_000m, [3] = 9_600_000m, [4] = 12_000_000m, [5] = 15_000_000m,
            [6] = 11_250_000m, [7] = 8_437_500m, [8] = 6_328_130m, [9] = 4_746_100m, [10] = 3_559_580m
        },
        ThreePerson: new Dictionary<int, decimal>
        {
            [1] = 6_750_000m, [2] = 9_000_000m, [3] = 12_000_000m, [4] = 8_000_000m, [5] = 5_333_330m, [6] = 3_555_560m
        },
        Metaliminal: new Dictionary<int, decimal>
        {
            [1] = 4_915_200m, [2] = 6_144_000m, [3] = 7_680_000m, [4] = 9_600_000m, [5] = 12_000_000m,
            [6] = 9_000_000m, [7] = 6_750_000m, [8] = 5_062_500m, [9] = 3_796_880m, [10] = 2_847_660m
        },
        AarTotal: new Dictionary<int, decimal>
        {
            [1] = 27_648_000m, [2] = 34_560_000m, [3] = 43_200_000m, [4] = 54_000_000m, [5] = 67_500_000m,
            [6] = 50_625_000m, [7] = 37_968_750m, [8] = 28_476_570m, [9] = 21_357_420m, [10] = 16_018_080m
        },
        // Waves 1-3, 4-6, 7-9 — the three threat bands, at N = 5 (domain/homefronts.md §3.2).
        AarWaveBandAtN5: new Dictionary<int, decimal> { [1] = 5_000_000m, [2] = 7_500_000m, [3] = 10_000_000m });

    private static readonly IReadOnlyList<Version> _versions = [_23_02];

    /// <summary>Which curve a homefront kind (<see cref="HomefrontCatalogue.KindByDungeonId"/>) pays from, or null
    /// for a kind this table does not know.</summary>
    public static HomefrontCurve? CurveFor(string kind) => kind switch
    {
        "Raid" or "Dread Assault" or "Emergency Aid" or "Suspicious Signal" => HomefrontCurve.FivePerson,
        "Salvage Research" or "Stabilize Rift" or "Traffic Stop" => HomefrontCurve.ThreePerson,
        "Metaliminal Meteoroid" => HomefrontCurve.Metaliminal,
        "Abyssal Artifact Recovery" => HomefrontCurve.Aar,
        _ => null
    };

    /// <summary>
    /// The expected payout for one character, or null when there is nothing to expect yet: not ticked in
    /// (<paramref name="inSiteAtCompletion"/>), N not known, no table for <paramref name="atUtc"/>, N outside the
    /// table (never a guess), or — for every curve but AAR — the site is not <see cref="HomefrontOutcome.Completed"/>.
    /// AAR has no <see cref="HomefrontOutcome"/> of its own: <paramref name="completedWaveCount"/> above zero is
    /// itself the "it paid" fact, since a failed site still keeps whatever waves it already cleared.
    /// </summary>
    public static (decimal Amount, string Version, DateTime EffectiveFromUtc)? TryGetExpected(
        string? kind, bool? inSiteAtCompletion, int? n, HomefrontOutcome? outcome, int? completedWaveCount, DateTime atUtc)
    {
        if (kind is null || inSiteAtCompletion != true || n is not { } count || count <= 0)
            return null;

        HomefrontCurve? curve = CurveFor(kind);
        if (curve is null || _VersionFor(atUtc) is not { } version)
            return null;

        if (curve == HomefrontCurve.Aar)
            return completedWaveCount is { } waves && waves > 0 && _AarAmount(version, count, waves) is { } aarAmount
                ? (aarAmount, version.Label, version.EffectiveFromUtc)
                : null;

        return outcome == HomefrontOutcome.Completed ? TryGetTableAmount(kind, count, atUtc) : null;
    }

    /// <summary>What N pays on this curve, with no regard for outcome or the tick — "if completed" preview text
    /// while a homefront is still running (ET-231), never what counts towards TOTAL ISK: <see cref="TryGetExpected"/>
    /// is the only figure that counts. Null for AAR, which has no one figure per N without a wave count too.</summary>
    public static (decimal Amount, string Version, DateTime EffectiveFromUtc)? TryGetTableAmount(string? kind, int? n, DateTime atUtc)
    {
        if (kind is null || n is not { } count || count <= 0 || CurveFor(kind) is not { } curve || curve == HomefrontCurve.Aar
            || _VersionFor(atUtc) is not { } version)
            return null;

        IReadOnlyDictionary<int, decimal> table = curve switch
        {
            HomefrontCurve.FivePerson => version.FivePerson,
            HomefrontCurve.ThreePerson => version.ThreePerson,
            _ => version.Metaliminal
        };
        return table.TryGetValue(count, out decimal amount) ? (amount, version.Label, version.EffectiveFromUtc) : null;
    }

    private static Version? _VersionFor(DateTime atUtc) =>
        _versions.Where(version => version.EffectiveFromUtc <= atUtc)
            .OrderByDescending(version => version.EffectiveFromUtc)
            .FirstOrDefault();

    /// <summary>Waves 1..<paramref name="completedWaves"/>, each at its threat band's N = 5 rate scaled by the same
    /// ratio the 9-wave total scales by at <paramref name="n"/> — the only N-scaling AAR's wave rates are documented
    /// at (domain/homefronts.md §3.2 gives the band rates only at N = 5). Null when <paramref name="n"/> is outside
    /// the table, the same "never a guess" rule every other curve follows.</summary>
    private static decimal? _AarAmount(Version version, int n, int completedWaves)
    {
        if (!version.AarTotal.TryGetValue(n, out decimal totalAtN) || !version.AarTotal.TryGetValue(5, out decimal totalAt5))
            return null;

        decimal ratio = totalAtN / totalAt5;
        decimal sum = 0m;
        for (int wave = 1; wave <= Math.Min(completedWaves, 9); wave++)
        {
            int band = wave <= 3 ? 1 : wave <= 6 ? 2 : 3;
            sum += version.AarWaveBandAtN5[band] * ratio;
        }

        return sum;
    }
}
