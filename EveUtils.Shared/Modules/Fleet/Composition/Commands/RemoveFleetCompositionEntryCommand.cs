using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Removes a fit-entry from a role-group. Gated on owner-or-manage.</summary>
public sealed record RemoveFleetCompositionEntryCommand(
    long EntryId,
    int ActingCharacterId) : ICommand<Result>;
