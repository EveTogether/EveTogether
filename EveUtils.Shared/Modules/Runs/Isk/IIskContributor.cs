using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>One source of a run's ISK (ET-256). Registered once in <see cref="IskContributors"/>; no screen calls
/// it directly.</summary>
public interface IIskContributor
{
    /// <summary>The share of the total this contributor answers for — one contributor per source.</summary>
    IskSource Source { get; }

    /// <summary>What this source added to or took from an activity made of <paramref name="runs"/> — one run, or
    /// every run of a group. <paramref name="nowUtc"/> stands in for the stop of a run that is still going. Null when
    /// the source has nothing to say about these runs, which is not the same as a contribution of zero.</summary>
    IskContribution? Contribute(IReadOnlyList<RunIskFacts> runs, DateTime nowUtc);
}
