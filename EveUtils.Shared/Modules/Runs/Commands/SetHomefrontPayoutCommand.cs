using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Type what this homefront run's own character actually received, over the table's figure (ET-231, ET-271) — never
/// anything read from the wallet, only what the pilot says. Writes the one <see cref="Entities.RunParameter"/> row
/// <see cref="Enums.RunParameterKey.FixedPayout"/>; a run already carrying one has it replaced, never added to, so
/// <c>HomefrontPayoutIskContributor</c> never sees two figures for the same character.
/// </summary>
/// <param name="RunId">This client's own run — never a group-mate's, the same rule every run-owning command
/// follows.</param>
/// <param name="AmountIsk">What arrived, or null to drop a typed figure and go back to the table's.</param>
public sealed record SetHomefrontPayoutCommand(Guid RunId, decimal? AmountIsk) : ICommand<Result>;
