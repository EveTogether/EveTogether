using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Clears an internal fleet's link to a live in-game ESI fleet: the in-game fleet is gone, so the
/// stored <c>EsiFleetId</c> must be wiped server-side or it keeps a dead reference around that other clients would
/// still poll for ESI data. Storage-role only — no ESI call is made. Inverse of
/// <see cref="CoupleFleetToEsiCommand"/>; owner-only, <c>fleet.edit</c> gated. Idempotent on an already
/// uncoupled fleet.
/// </summary>
[RequiresPermission(FleetPermissions.Edit)]
public sealed record UncoupleFleetFromEsiCommand(long FleetId, int ActingCharacterId) : ICommand<Result>;
