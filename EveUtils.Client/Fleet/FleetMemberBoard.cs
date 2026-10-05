using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.Fleet;

/// <summary>One fleet member as this client can tell it (ET-440): the reason behind their row and what it rests on.</summary>
public sealed record FleetMateStatus(
    int CharacterId,
    FleetMemberStatusReason Reason,
    string? System,
    SharedMetrics? Shares,
    bool? IsConnected,
    DateTimeOffset? LastSeenAt);

/// <summary>
/// Everything a screen needs to say why a fleet member reads the way they do (ET-440): what the fleet stream last said
/// about each of them, and the roster with the server's word on who is connected. The run window used to know only
/// the members who sent a location, so a pilot who shared nothing was simply missing and one who shared no system
/// read "not sharing a system" whatever the cause.
///
/// The subscriber only writes a dictionary under a lock — the bus waits on it inside every command. The roster is
/// read on demand and at most once per <see cref="RosterRefreshInterval"/> per fleet.
/// </summary>
public sealed class FleetMemberBoard : ISingletonService, IDisposable
{
    private static readonly TimeSpan RosterRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly IFleetParticipation _participation;
    private readonly IFleetTransportClient? _transport;
    private readonly IDisposable _subscription;
    private readonly Lock _gate = new();
    private readonly Dictionary<(long FleetId, int CharacterId), Heard> _heard = [];
    private readonly Dictionary<long, (IReadOnlyList<FleetMemberInfo> Members, DateTimeOffset At)> _rosters = [];
    private readonly HashSet<long> _rosterLoading = [];

    public FleetMemberBoard(IEventBus eventBus, IFleetParticipation participation, IFleetTransportClient? transport = null)
    {
        _participation = participation;
        _transport = transport;
        _subscription = eventBus.Subscribe<FleetMetricEvent>(_OnMetric);
    }

    /// <summary>Raised off the UI thread when a roster read lands, so an open screen can redraw.</summary>
    public event Action<long>? RosterChanged;

    public void Dispose() => _subscription.Dispose();

    /// <summary>Every member of the fleet this client knows of — the roster, plus anyone heard from who is not on it
    /// yet — each with their reason. Starts a roster read when the one held is stale.</summary>
    public IReadOnlyList<FleetMateStatus> Read(long fleetId, DateTimeOffset now)
    {
        _RefreshRosterIfDue(fleetId, now);

        lock (_gate)
        {
            IReadOnlyList<FleetMemberInfo> roster = _rosters.TryGetValue(fleetId, out var held) ? held.Members : [];
            IEnumerable<int> ids = roster.Where(member => !member.IsExternal).Select(member => member.CharacterId)
                .Concat(_heard.Keys.Where(key => key.FleetId == fleetId).Select(key => key.CharacterId))
                .Distinct();

            return [.. ids.Select(characterId =>
            {
                FleetMemberInfo? member = roster.FirstOrDefault(entry => entry.CharacterId == characterId);
                Heard heard = _heard.GetValueOrDefault((fleetId, characterId)) ?? Heard.Nothing;
                FleetMemberPresenceState presence = FleetMemberPresence.Read(
                    null, heard.Presence, FleetMemberPresence.IsSilent(heard.At, now));
                return new FleetMateStatus(characterId,
                    FleetMemberStatus.Read(member?.IsConnected, heard.At, presence, heard.Shares, heard.System, now),
                    heard.System, heard.Shares, member?.IsConnected, heard.At ?? member?.LastSeenAt);
            })];
        }
    }

    private void _OnMetric(FleetMetricEvent integrationEvent)
    {
        MetricSample sample = integrationEvent.Data;
        var key = (sample.FleetId, sample.CharacterId);
        lock (_gate)
        {
            Heard heard = (_heard.GetValueOrDefault(key) ?? Heard.Nothing) with { At = DateTimeOffset.UtcNow };
            _heard[key] = sample.Kind switch
            {
                MetricKind.Presence => heard with { Presence = Enum.IsDefined((PresenceState)(int)sample.Value)
                    ? (PresenceState)(int)sample.Value
                    : PresenceState.Unknown },
                MetricKind.Shares => heard with { Shares = (SharedMetrics)(int)sample.Value },
                MetricKind.Location => heard with { System = sample.Text },
                _ => heard,
            };
        }
    }

    private void _RefreshRosterIfDue(long fleetId, DateTimeOffset now)
    {
        if (_transport is null
            || _participation.Current.FirstOrDefault(entry => entry.FleetId == fleetId && !entry.ClientOnly)
                is not { ServerAddress: { } server } participant)
            return;

        lock (_gate)
        {
            if ((_rosters.TryGetValue(fleetId, out var held) && now - held.At < RosterRefreshInterval)
                || !_rosterLoading.Add(fleetId))
                return;
        }

        _ = _LoadRosterAsync(server, fleetId, participant.CharacterId, now);
    }

    private async Task _LoadRosterAsync(string server, long fleetId, int actingCharacterId, DateTimeOffset now)
    {
        try
        {
            IReadOnlyList<FleetMemberInfo> members = await _transport!.ListMembersAsync(server, fleetId, actingCharacterId);
            lock (_gate)
            {
                // A failed read comes back as an empty list, and a fleet is never empty: keep the roster last read.
                if (members.Count == 0 && _rosters.TryGetValue(fleetId, out var held))
                    members = held.Members;
                _rosters[fleetId] = (members, now);
            }
            RosterChanged?.Invoke(fleetId);
        }
        catch (Exception)
        {
            // An unreachable server keeps the roster last read; the stream still tells who is reporting.
        }
        finally
        {
            lock (_gate)
                _rosterLoading.Remove(fleetId);
        }
    }

    private sealed record Heard(DateTimeOffset? At, PresenceState Presence, SharedMetrics? Shares, string? System)
    {
        public static readonly Heard Nothing = new(null, PresenceState.Unknown, null, null);
    }
}
