using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Takes the fleet commander's homefront attendance list off the wire and writes it onto this client's own runs under
/// its group code (ET-230) — whether or not a run window is open, the way <see cref="FleetRunGroupCodeCoordinator"/>
/// applies a discard. Every client applies it to its own rows only, and never overrides it with a guess of its own:
/// only the commander's list is ever written here.
///
/// A list counts only from whoever commands the fleet right now, as this client's own participation reads the roster —
/// after a handover the list stays one list, and the new commander's is the one that counts. A client that cannot tell
/// who commands the fleet yet lets the list go; the commander's window sends it again within half a minute.
/// </summary>
public sealed class FleetRunAttendance : ISingletonService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _receivedAtUtc = new(StringComparer.Ordinal);
    private readonly IDispatcher _dispatcher;
    private readonly IFleetParticipation _participation;
    private readonly ICharacterRegistry _characters;
    private readonly IDisposable _subscription;

    public FleetRunAttendance(IEventBus eventBus, IDispatcher dispatcher, IFleetParticipation participation,
        ICharacterRegistry characters)
    {
        _dispatcher = dispatcher;
        _participation = participation;
        _characters = characters;
        _subscription = eventBus.Subscribe<FleetRunAttendanceEvent>(_OnAttendanceAsync);
    }

    /// <summary>A commander's list for this group code was just written onto this client's runs. Raised on whichever
    /// thread the bus delivered on.</summary>
    public event Action<string>? Applied;

    public void Dispose() => _subscription.Dispose();

    /// <summary>When this client last received the commander's list for <paramref name="groupCode"/>, or null when it
    /// has not in this session — the "received" a member's window shows beside the commander's own time.</summary>
    public DateTime? ReceivedAtUtc(string groupCode)
    {
        lock (_gate)
            return _receivedAtUtc.TryGetValue(groupCode, out DateTime receivedAtUtc) ? receivedAtUtc : null;
    }

    /// <summary>The commander's message as the decision it stands for — the one conversion both sides use, so what the
    /// commander wrote on their own runs and what a member writes on theirs cannot differ.</summary>
    public static RunAttendanceDecision ToDecision(RunGroupAttendance attendance, int commanderCharacterId) => new(
        attendance.Characters, attendance.NotOnRosterCount, AttendanceSource.FleetCommander, commanderCharacterId,
        DateTimeOffset.FromUnixTimeMilliseconds(attendance.UnixMs).UtcDateTime);

    private async Task _OnAttendanceAsync(FleetRunAttendanceEvent integrationEvent, CancellationToken cancellationToken)
    {
        RunGroupAttendance attendance = integrationEvent.Data;
        if (integrationEvent.CharacterId is not { } sender || string.IsNullOrEmpty(attendance.GroupCode)
            || attendance.NotOnRosterCount < 0 || !_IsCommanderOf(attendance.FleetId, sender))
            return;

        long[] own = [.. (await _characters.GetAllAsync(cancellationToken))
            .Select(character => character.EsiCharacterId)
            .OfType<int>()
            .Select(characterId => (long)characterId)];
        Result<int> applied = await _dispatcher.Send(
            new SetRunAttendanceCommand(ToDecision(attendance, sender), own, attendance.GroupCode), cancellationToken);
        if (!applied.IsSuccess)
            return;

        lock (_gate)
            _receivedAtUtc[attendance.GroupCode] = DateTime.UtcNow;
        Applied?.Invoke(attendance.GroupCode);
    }

    private bool _IsCommanderOf(long fleetId, int characterId) =>
        _participation.Current.Any(participant => participant.FleetId == fleetId
                                                  && participant.FleetCommanderCharacterId == characterId);
}
