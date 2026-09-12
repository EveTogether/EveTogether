namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>
/// Every source of a run's ISK, and the one place TOTAL ISK is added up (ET-256). A new source — consumables, mining, a
/// homefront payout — is one contributor registered here and one section module claiming it
/// (<c>RunSectionModules</c>); the runs overview, the day total, the detail screen, the run window, UNFINISHED and
/// "ISK today" show its share without being touched, and saved activities pick it up at the next start
/// (<see cref="Signature"/>).
/// </summary>
public static class IskContributors
{
    public static IReadOnlyList<IIskContributor> All { get; } =
    [
        new BountyIskContributor(),
        new LootIskContributor(),
        new RewardIskContributor(),
        new ConsumableIskContributor(),
        new MiningIskContributor(),
        new HomefrontPayoutIskContributor()
    ];

    /// <summary>Which sources a stored breakdown was built by. A summary built by another set is out of date and is
    /// rebuilt at startup, so registering a source reaches activities saved before it existed.</summary>
    public static string Signature { get; } = string.Join(",", All.Select(contributor => contributor.Source));

    /// <param name="nowUtc">Stands in for the stop of a run that is still going.</param>
    public static IskBreakdown Breakdown(IReadOnlyList<RunIskFacts> runs, DateTime nowUtc) =>
        new([.. All.Select(contributor => contributor.Contribute(runs, nowUtc)).OfType<IskContribution>()]);
}
