using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>Thread-safe <see cref="IEsiAvailabilityState"/>; Available until a poll reports otherwise.</summary>
public sealed class EsiAvailabilityState : IEsiAvailabilityState, ISingletonService
{
    private volatile EsiAvailability _current = EsiAvailability.Available;

    public EsiAvailability Current => _current;

    public bool IsUsable => _current == EsiAvailability.Available;

    public void Set(EsiAvailability availability) => _current = availability;
}
