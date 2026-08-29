using System.Linq;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Holds the current membership-driven participation set. The fleet view model writes it from the loaded
/// listing; the <see cref="FleetMetricPublisher"/> reads it each tick. A plain volatile snapshot swap keeps reads
/// lock-free on the 1 Hz publish path.
/// </summary>
public sealed class FleetParticipation : IFleetParticipation, ISingletonService
{
    private volatile IReadOnlyList<FleetParticipant> _current = [];

    public IReadOnlyList<FleetParticipant> Current => _current;

    public void Set(IReadOnlyList<FleetParticipant> participants) => _current = participants;

    // Same volatile snapshot swap as Set: the publish path never sees a half-written set, and the next listing reload
    // rewrites the whole thing from the roster anyway, so this only has to hold until then.
    public void Remove(long fleetId, int characterId) =>
        _current = [.. _current.Where(p => p.FleetId != fleetId || p.CharacterId != characterId)];
}
