using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Archives a fleet on the server's own authority, without a creator behind it: the cleanup sweep retiring a fleet that
/// went quiet (ET-383). <see cref="ArchivedAt"/> doubles as the clock the hard-delete window counts from, so a sweep
/// run against a supplied "now" stays deterministic. A player ending their own fleet uses
/// <see cref="DisbandFleetCommand"/>.
/// </summary>
public sealed record ArchiveFleetCommand(long FleetId, DateTimeOffset ArchivedAt) : ICommand<Result>;
