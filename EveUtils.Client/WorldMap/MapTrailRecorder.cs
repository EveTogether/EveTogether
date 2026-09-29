using System;
using System.Collections.Concurrent;
using EveUtils.Client.Fleet;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Client.WorldMap;

/// <summary>
/// Keeps a <see cref="MapTrail"/> per character from the moment the app starts, whether or not the map is open — a
/// "since app start" trail is only true if something was listening all along. It reads the merged positions
/// (<see cref="IFleetPositionSource"/>) and so records a step whichever source reported it. What the ESI location poll
/// or a missed game log line skipped is not filled in: the trail shows two systems that are not neighbours, and the
/// map draws that jump dotted (see <c>StarMapControl.Trail</c>).
/// </summary>
public sealed class MapTrailRecorder : ISingletonService, IDisposable
{
    private readonly IFleetPositionSource _positions;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<int, MapTrail> _trails = new();

    public MapTrailRecorder(IFleetPositionSource positions, TimeProvider clock)
    {
        _positions = positions;
        _clock = clock;
        StartedAt = clock.GetUtcNow();
        foreach (FleetPositionDto known in positions.GetPositions())
            TrailOf(known.CharacterId).Add(known.SolarSystemId, known.ObservedAt);
        positions.PositionChanged += _OnPositionChanged;
    }

    public DateTimeOffset StartedAt { get; }

    public MapTrail TrailOf(int characterId) => _trails.GetOrAdd(characterId, _ => new MapTrail());

    public void ResetTrail(int characterId) => TrailOf(characterId).Reset(_clock.GetUtcNow());

    public void Dispose() => _positions.PositionChanged -= _OnPositionChanged;

    private void _OnPositionChanged(FleetPositionDto position) => TrailOf(position.CharacterId).Add(position.SolarSystemId, position.ObservedAt);
}
