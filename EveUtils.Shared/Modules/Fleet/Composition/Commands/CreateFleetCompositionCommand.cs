using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>
/// Creates a composition owned by <see cref="ActingCharacterId"/>.
/// Anyone may create their own; returns the new composition's id. <see cref="IsClientOnly"/> makes it a local-only
/// doctrine (client SQLite, owner-only).
/// </summary>
public sealed record CreateFleetCompositionCommand(
    string Name,
    string? Description,
    bool IsClientOnly,
    int ActingCharacterId) : ICommand<Result<long>>;
