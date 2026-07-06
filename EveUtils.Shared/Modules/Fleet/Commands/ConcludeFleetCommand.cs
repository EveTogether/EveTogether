using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Concludes a fleet (2026-06-04): flips its <see cref="Entities.FleetActivation"/> to
/// <see cref="Entities.FleetActivation.Concluded"/> — a finished fleet kept for history. A concluded fleet
/// broadcasts nothing, can no longer be joined or started, and its members no longer count toward the
/// "one active fleet per character" rule (so they are free to join the next op). Only the creator may conclude it
/// (enforced on <see cref="ActingCharacterId"/> in the handler); <c>fleet.edit</c> is gated server-side.
/// Allowed only from <see cref="Entities.FleetActivation.Active"/> — a Forming fleet never started, so it is
/// cancelled by disbanding it instead. Idempotent on an already-concluded fleet.
/// </summary>
[RequiresPermission(FleetPermissions.Edit)]
public sealed record ConcludeFleetCommand(long FleetId, int ActingCharacterId) : ICommand<Result>;
