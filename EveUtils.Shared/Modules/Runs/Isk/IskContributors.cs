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

    /// <summary>Which sources a stored breakdown was built by, and by which revision of the rules. A summary built by
    /// another set is out of date and is rebuilt at startup, so registering a source reaches activities saved before
    /// it existed.</summary>
    public static string Signature { get; } = $"{string.Join(",", All.Select(contributor => contributor.Source))};r{Revision}";

    // Raised whenever stored summaries may be wrong without any source changing. 2 (ET-271): a typed homefront payout
    // moved from Rewards to HomefrontPayout, and an outcome, attendance or payout set after SAVE never rebuilt its
    // summary before, so every activity saved until now is added up again once. 3 (ET-274): a homefront payout counts
    // once per character, and the startup fold of duplicate runs (HF-DYB4: nine runs for five characters) must reach
    // the summaries built over them.  4 (ET-296): every activity also stores what each character of it earned, so the
    // totals can show the pilot's own share rather than the whole group's.
    private const int Revision = 4;

    /// <param name="nowUtc">Stands in for the stop of a run that is still going.</param>
    public static IskBreakdown Breakdown(IReadOnlyList<RunIskFacts> runs, DateTime nowUtc) =>
        new([.. All.Select(contributor => contributor.Contribute(runs, nowUtc)).OfType<IskContribution>()]);

    /// <summary>
    /// The same activity split over the characters who flew it (ET-296) — what lets every total show the pilot's own
    /// share, the sum over his own local characters, while the detail screen goes on showing the group's.
    ///
    /// Every source but one adds up run by run, so a character's share is simply <see cref="Breakdown"/> over his own
    /// runs. Rewards are the exception: a mission pays its stated line once however many of the group's runs carry a
    /// copy of it (ET-210), so counting it per character would count it once per copy. A line is therefore handed to
    /// the first run that counts it and taken out of the rest — which makes the split add up, per source, to exactly
    /// what <see cref="Breakdown"/> gives for the same runs.
    /// </summary>
    /// <param name="runsInOrder">Every run of the activity, earliest first (<c>StartedAtUtc</c>, then <c>Id</c>): the
    /// order decides which character a duplicated reward line is handed to.</param>
    /// <param name="nowUtc">Stands in for the stop of a run that is still going.</param>
    public static IReadOnlyDictionary<long, IskBreakdown> BreakdownByCharacter(
        IReadOnlyList<RunIskFacts> runsInOrder, DateTime nowUtc)
    {
        HashSet<RunIskParameter> claimed = [];
        RunIskFacts[] owned = [.. runsInOrder.Select(run => run with
        {
            Parameters = [.. run.Parameters.Where(parameter =>
                !RewardIskContributor.Counts(parameter, run.StoppedAtUtc ?? nowUtc) || claimed.Add(parameter))]
        })];
        return owned
            .GroupBy(run => run.CharacterId)
            .ToDictionary(character => character.Key, character => Breakdown([.. character], nowUtc));
    }
}
