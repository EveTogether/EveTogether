using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Client.Runs;

/// <summary>Where the runs overview's BY ACTIVITY list reads its figures from (ET-517).</summary>
public interface IActivityStatsSource
{
    Task<ActivityStatsOverview> ReadAsync(ActivityStatsRequest request, CancellationToken cancellationToken = default);

    /// <summary>The figures of one activity wherever the list happens to be: what the run drawer compares a run with, and
    /// what the detail shows for an activity the list does not hold. Without enough runs it has no figure, like any other.</summary>
    Task<ActivityStats> ReadKeyAsync(ActivityStatsKey key, IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default);
}

/// <param name="FromUtc">The start of the range on screen, inclusive: the figures of YOURS are taken over it.</param>
/// <param name="ToUtc">The end of the range, exclusive.</param>
/// <param name="ServerAddress">The server tab the list is for, null for Local.</param>
/// <param name="CharacterIds">The pilot's own characters: whichever of them is connected to a server asks it.</param>
public sealed record ActivityStatsRequest(DateTime FromUtc, DateTime ToUtc, string? ServerAddress, IReadOnlyList<int> CharacterIds);

/// <param name="Status">Why the list is empty or short when it is, null otherwise: a server tab that cannot be read says so.</param>
public sealed record ActivityStatsOverview(IReadOnlyList<ActivityStats> Activities, string? Status = null);

/// <summary>
/// One activity as the overview lists it (ET-517).
/// </summary>
/// <param name="SiteName">As the pilot's latest run named it; null for an activity only a server knows.</param>
/// <param name="LastFlownUtc">When the pilot last flew it; null for an activity only a server knows.</param>
/// <param name="Yours">The pilot's own runs over the range on screen; null on a server tab.</param>
/// <param name="Basis">The figure the row's TYPICAL, band and ISK/h read: a server's shared figure when there is one, else
/// the pilot's own runs. Null on a server tab whose server has no figure for it.</param>
/// <param name="Own">The pilot's own runs over the statistic window, the fallback and the dots in the distribution; null
/// on a server tab.</param>
/// <param name="Reason">Why <see cref="Basis"/> is the pilot's own runs and not a server's.</param>
/// <param name="Answers">Every server that gave a figure, for the source chip's list.</param>
/// <param name="EffectiveRuns">What a server counts behind the key: the number "3 of 5 needed" is told from when there is
/// no figure.</param>
public sealed record ActivityStats(ActivityStatsKey Key, string? SiteName, DateTime? LastFlownUtc, EarningsFiguresDto? Yours,
    ExpectedEarnings? Basis, ExpectedEarnings? Own, OwnRunsReason Reason, IReadOnlyList<ExpectedEarnings> Answers,
    double EffectiveRuns, DateTime ReadAtUtc);
