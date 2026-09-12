using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Confirm or type what this homefront run's own character actually received (ET-231) — never anything read from the
/// wallet, only what the pilot says. Writes the one <see cref="Entities.RunParameter"/> row
/// <see cref="Enums.RunParameterKey.FixedPayout"/> already had no producer for; a run already carrying one has it
/// replaced, never added to, so <c>RewardIskContributor</c> never sums two figures for the same character.
/// </summary>
/// <param name="RunId">This client's own run — never a group-mate's, the same rule every run-owning command
/// follows.</param>
/// <param name="AmountIsk">What arrived, confirmed from the expected figure or typed by the pilot.</param>
public sealed record SetHomefrontPayoutCommand(Guid RunId, decimal AmountIsk) : ICommand<Result>;
