using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The expected yield per activity out of the own saved runs (ET-513), the most-flown first: of
/// <paramref name="Key"/> alone, or of every activity with a key when it is null. <paramref name="Window"/> null takes
/// the policy's 90 days with the 365-day fallback; a window given is that window only.</summary>
public sealed record GetActivityEarningsQuery(ActivityStatsKey? Key = null, TimeSpan? Window = null)
    : IQuery<Result<IReadOnlyList<ActivityEarningsDto>>>;
