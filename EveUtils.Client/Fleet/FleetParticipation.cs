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
}
