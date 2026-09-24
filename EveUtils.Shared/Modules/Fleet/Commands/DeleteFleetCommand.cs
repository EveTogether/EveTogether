using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Hard-deletes a fleet with its wings, squads, members and invites (FK cascade), on the server's own authority: the
/// cleanup sweep removing a long-archived fleet, or the control panel's purge (ET-383). The caller has checked the
/// right to; no player command reaches this.
/// </summary>
public sealed record DeleteFleetCommand(long FleetId) : ICommand<Result>;
