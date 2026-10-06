using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Registers an escalation on a run that is already saved (ET-453) — the run window can only do it before SAVE.
/// Adds the escalation's rows under a new entry beside any the run already carries, and signals the run changed.</summary>
public sealed record RegisterRunEscalationCommand(
    Guid RunId, string SiteName, int? DungeonId, string DestinationSystem, int? DestinationSolarSystemId,
    DateTime ExpiresAtUtc) : ICommand<Result>;
