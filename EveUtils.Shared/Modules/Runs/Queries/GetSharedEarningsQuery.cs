using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The expected yield of an activity out of the outcomes a server handed out (ET-516), valued at today's price
/// like the own runs. <paramref name="Loot"/> is the same activity over the same window: the outcomes carry the fixed
/// ISK and the loot comes apart, so the two lists are read side by side. <paramref name="Window"/> is the span the
/// outcomes were asked over and <paramref name="Label"/> what the figures are called when shown.</summary>
public sealed record GetSharedEarningsQuery(ActivityOutcomesDto Outcomes, ActivityOutcomeLootDto Loot, TimeSpan Window,
    EarningsWindow Label = EarningsWindow.Recent) : IQuery<Result<EarningsFiguresDto>>;
