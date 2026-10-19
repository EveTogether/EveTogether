using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.RunOutcomes.Commands;

/// <summary>Removes every outcome a contributor shared, with their loot lines: the opt-out (ET-514). Returns how many.
/// Not gated by the server's switch. An activity a fleet mate also flew goes with it when this contributor stored it:
/// one outcome per activity, and it was theirs.</summary>
public sealed record WithdrawRunOutcomesCommand(string ContributorBucket) : ICommand<Result<int>>;
