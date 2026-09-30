using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.Runs.Attendance;
using EveUtils.Shared.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.WorldMap;

/// <summary>
/// The fleets come from the participation set the fleet listing keeps (active fleets only), the roster is read the
/// way the attendance list reads it, and the in-game fleet the boss's ESI poll saw is added on top — that is where a
/// member who does not use EVE Together comes from. Nothing is stored.
/// </summary>
public sealed class MapFleetSource(IServiceProvider services, IFleetParticipation participation, InGameFleetRosters inGame,
    IFleetRosterWatch rosterWatch) : IMapFleetSource, ISingletonService
{
    public IReadOnlyList<MapFleetChoice> ActiveFleets() =>
    [
        .. participation.Current
            .GroupBy(participant => (participant.FleetId, participant.ServerAddress))
            .Select(fleet => new MapFleetChoice(fleet.Key.FleetId,
                fleet.Select(participant => participant.FleetName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Fleet {fleet.Key.FleetId}",
                fleet.Key.ServerAddress))
            .OrderBy(fleet => fleet.Name, StringComparer.OrdinalIgnoreCase)
    ];

    public Task RefreshActiveFleetsAsync() =>
        services.GetService<FleetParticipationRefresher>()?.RefreshAsync() ?? Task.CompletedTask;

    public int? CommanderOf(MapFleetChoice fleet) =>
        participation.Current
            .Where(participant => participant.FleetId == fleet.FleetId && participant.ServerAddress == fleet.ServerAddress)
            .Select(participant => participant.FleetCommanderCharacterId)
            .FirstOrDefault(commander => commander is not null);

    public async Task<IReadOnlyCollection<int>> MembersOfAsync(MapFleetChoice fleet)
    {
        IReadOnlyList<RosterCharacter> roster = await AttendanceRoster.ReadAsync(services, fleet.FleetId) ?? [];
        return
        [
            .. roster.Select(member => checked((int)member.CharacterId))
                .Concat(inGame.MembersOf(fleet.ServerAddress, fleet.FleetId))
                .Distinct()
        ];
    }

    public Task<string?> NameOfAsync(int characterId) => AttendanceRoster.NameOfAsync(services, characterId);

    public IDisposable WatchRosters(Action<FleetRosterChange> rosterChanged) => rosterWatch.Subscribe(rosterChanged);
}
