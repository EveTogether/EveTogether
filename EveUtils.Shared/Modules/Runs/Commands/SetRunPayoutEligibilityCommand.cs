using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Exclude a character from the payout split, or put them back in. Deliberately cannot touch
/// <c>Run.IsParticipant</c>: the hauler who fetched ore during the site is excluded from the ISK and still
/// registers their loot, and folding the two into one switch would erase that distinction (ET-105).
/// </summary>
public sealed record SetRunPayoutEligibilityCommand(Guid RunId, bool IsPayoutEligible) : ICommand<Result>;
