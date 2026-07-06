using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Renames/re-describes an existing composition. Gated on owner-or-manage.</summary>
public sealed record EditFleetCompositionCommand(
    long CompositionId,
    string Name,
    string? Description,
    int ActingCharacterId) : ICommand<Result>;
