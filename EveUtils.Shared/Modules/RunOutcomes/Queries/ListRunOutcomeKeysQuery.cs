using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;

namespace EveUtils.Shared.Modules.RunOutcomes.Queries;

/// <summary>Every activity with shared outcomes since <paramref name="Since"/> (at most 365 days back) and how many,
/// the most-flown first, for the runs overview (ET-517). Refused with <c>PERMISSION_DENIED</c> while the server's switch
/// is off.</summary>
public sealed record ListRunOutcomeKeysQuery(DateOnly Since) : IQuery<Result<IReadOnlyList<RunOutcomeKeyCountDto>>>;
