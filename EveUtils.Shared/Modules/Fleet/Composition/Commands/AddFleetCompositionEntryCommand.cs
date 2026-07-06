using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Adds a fit-entry (an allowed fit + optional per-fit minimum) to a role-group. The
/// <see cref="Fit"/> is a self-contained snapshot. Gated on owner-or-manage; appended at the end.
/// Returns the new entry's id.</summary>
public sealed record AddFleetCompositionEntryCommand(
    long RoleId,
    FitReference Fit,
    int? EntryMinCount,
    int ActingCharacterId) : ICommand<Result<long>>;
