using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Write the one fact <see cref="StopRunsLeftRunningCommand"/> needs to give an honest stop time to a run this
/// process never got to close out itself (ET-254): that the run was still on the clock at <paramref name="AtUtc"/>.
///
/// Sent roughly once a minute while a run is <see cref="Enums.RunState.Running"/> (<c>ActivityWindowViewModel</c>'s
/// own clock tick), never on every tick — a run left running for hours would otherwise write this every second for
/// nothing <see cref="StopRunsLeftRunningCommandHandler"/> could ever read back more precisely than a minute apart.
///
/// Deliberately not <see cref="SetRunStoppedCommand"/> or any other run-changing command: this changes nothing a
/// pilot would recognise as a change. It does not bump <see cref="Entities.Run.Revision"/>, does not touch
/// <see cref="Enums.RunSyncState"/> (the written moment never crosses the wire, see
/// <see cref="Entities.Run.LastAliveAtUtc"/>), and raises no <see cref="Events.RunsChangedEvent"/> — every screen
/// that reads a run already redraws on its own clock tick, and one more event a minute, for every open window, for
/// a field none of them show, would be the one part of this ticket the app would feel.
/// </summary>
public sealed record TouchRunAliveCommand(Guid RunId, DateTime AtUtc) : ICommand<Result>;
