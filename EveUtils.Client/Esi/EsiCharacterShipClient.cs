using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Location;

namespace EveUtils.Client.Esi;

/// <summary>Active-ship ESI client over the shared, scope-aware ESI pivot.</summary>
public sealed class EsiCharacterShipClient(IEsiClient esi) : IEsiCharacterShipClient, ISingletonService
{
    private static readonly IReadOnlyList<string> ReadScopes = [LocationScopeCatalog.ReadShipType];

    public Task<EsiResult<EsiCharacterShip>> GetShipAsync(int characterId, CancellationToken cancellationToken = default) =>
        esi.GetAsync<EsiCharacterShip>($"/characters/{characterId}/ship/", characterId, ReadScopes, cancellationToken);
}
