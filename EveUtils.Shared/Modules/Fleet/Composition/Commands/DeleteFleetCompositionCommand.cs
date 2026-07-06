using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Deletes a composition with its roles and entries. Gated on owner-or-manage.</summary>
public sealed record DeleteFleetCompositionCommand(
    long CompositionId,
    int ActingCharacterId) : ICommand<Result>;
