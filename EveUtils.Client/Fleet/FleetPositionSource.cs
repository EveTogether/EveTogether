using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Esi;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Merges every position source into the newest position per character (ET-394): own characters from the gamelog
/// and the ESI location watch, fleet mates from their shared <see cref="MetricKind.Location"/> sample, and every
/// in-game fleet member — EVE Together user or not — from the roster <see cref="EsiFleetSyncService"/> polls.
///
/// Newest wins, with one exception: an ESI answer is served from a cache and can still show the system a pilot just
/// left, so it may not overrule a live source (gamelog, fleet sample) that saw a different system less than
/// <see cref="CachedSourceLag"/> earlier — otherwise every jump would flicker back for a poll.
///
/// Nothing here is stored: a location mate's sample only arrives when they opted in to sharing it, and the ESI fleet
/// system is passed through without touching <c>FleetMember.SolarSystemId</c>.
/// </summary>
public sealed class FleetPositionSource : IFleetPositionSource, ISingletonService, IDisposable
{
    // The fleet members and location endpoints are cached for 5 s and polled every 5–6 s, so an answer can be up to
    // ~11 s behind a jump; 15 s covers that without delaying a real move the gamelog missed by much.
    internal static readonly TimeSpan CachedSourceLag = TimeSpan.FromSeconds(15);

    private readonly Dictionary<int, FleetPositionDto> _positions = new();
    private readonly Lock _gate = new();
    private readonly SolarSystemIdResolver _systemIds;
    private readonly GamelogClientService? _gamelog;
    private readonly IDisposable _metricSubscription;
    private readonly ILogger _logger;

    public event Action<FleetPositionDto>? PositionChanged;

    public FleetPositionSource(IEventBus eventBus, SolarSystemIdResolver systemIds, GamelogClientService? gamelog = null,
        ILogger<FleetPositionSource>? logger = null)
    {
        _systemIds = systemIds;
        _gamelog = gamelog;
        _logger = logger ?? NullLogger<FleetPositionSource>.Instance;

        _metricSubscription = eventBus.Subscribe<FleetMetricEvent>(evt => _Guarded(() => _OnFleetMetric(evt.Data)));
        if (_gamelog is not null)
        {
            _gamelog.GamelogLocationObserved += _OnGamelogLocation;
            _gamelog.EsiLocationObserved += _OnEsiLocation;
        }
    }

    public IReadOnlyList<FleetPositionDto> GetPositions()
    {
        lock (_gate)
            return [.. _positions.Values];
    }

    /// <summary>The in-game fleet roster of one poll. A member without a system (0) is skipped.</summary>
    internal void ObserveEsiFleet(IEnumerable<EsiFleetMember> members, DateTimeOffset polledAt)
    {
        foreach (var member in members.Where(member => member.CharacterId != 0 && member.SolarSystemId != 0))
            Observe(new FleetPositionDto(member.CharacterId, null, member.SolarSystemId, PositionSource.EsiFleet, polledAt));
    }

    /// <summary>Merges one sighting; true when it is a new position (a new character or another system).</summary>
    internal bool Observe(FleetPositionDto observed)
    {
        FleetPositionDto merged;
        lock (_gate)
        {
            if (!_positions.TryGetValue(observed.CharacterId, out var current))
            {
                merged = observed;
            }
            else
            {
                if (observed.ObservedAt <= current.ObservedAt)
                    return false;

                var name = observed.Name ?? current.Name;
                if (observed.SolarSystemId == current.SolarSystemId)
                {
                    var source = _IsCached(observed.Source) ? current.Source : observed.Source;
                    _positions[observed.CharacterId] = current with { Name = name, Source = source, ObservedAt = observed.ObservedAt };
                    return false;
                }

                if (_IsCached(observed.Source) && !_IsCached(current.Source)
                    && observed.ObservedAt - current.ObservedAt < CachedSourceLag)
                    return false;

                merged = observed with { Name = name };
            }

            _positions[observed.CharacterId] = merged;
        }

        PositionChanged?.Invoke(merged);
        return true;
    }

    public void Dispose()
    {
        _metricSubscription.Dispose();
        if (_gamelog is not null)
        {
            _gamelog.GamelogLocationObserved -= _OnGamelogLocation;
            _gamelog.EsiLocationObserved -= _OnEsiLocation;
        }
    }

    private static bool _IsCached(PositionSource source) => source is PositionSource.EsiLocation or PositionSource.EsiFleet;

    private void _OnGamelogLocation(int characterId, string characterName, string system, DateTime at) => _Guarded(() =>
    {
        if (_systemIds.Resolve(system) is { } solarSystemId)
            Observe(new FleetPositionDto(characterId, characterName, solarSystemId, PositionSource.Gamelog, _AsUtc(at)));
    });

    private void _OnEsiLocation(int characterId, string characterName, int solarSystemId, DateTime at) =>
        _Guarded(() => Observe(new FleetPositionDto(characterId, characterName, solarSystemId, PositionSource.EsiLocation, _AsUtc(at))));

    // A mate inside an abyssal run has left the map: their last k-space system is not where they are, so the sample
    // leaves the position as it was rather than confirming it. A sample from before ET-394 carries only the name.
    private void _OnFleetMetric(MetricSample sample)
    {
        if (sample.Kind != MetricKind.Location || sample.AbyssalAnchorMs > 0)
            return;

        var solarSystemId = sample.Value > 0 ? (int)sample.Value : _systemIds.Resolve(sample.Text);
        if (solarSystemId is not { } id)
            return;

        Observe(new FleetPositionDto(sample.CharacterId, null, id, PositionSource.FleetMetric,
            DateTimeOffset.FromUnixTimeMilliseconds(sample.UnixMs)));
    }

    // The gamelog and ESI times are UTC; the gamelog parser leaves the kind unspecified.
    private static DateTimeOffset _AsUtc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    // Every caller is someone else's loop — the gamelog reader, the event bus inside a publish, the ESI poll — so a
    // failed lookup (an SDE store being rebuilt) costs this one sighting, never theirs.
    private void _Guarded(Action observe)
    {
        try
        {
            observe();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not merge a fleet position.");
        }
    }
}
