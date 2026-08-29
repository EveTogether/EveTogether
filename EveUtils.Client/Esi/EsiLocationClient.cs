using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Location;

namespace EveUtils.Client.Esi;

/// <summary>
/// <see cref="IEsiLocationClient"/> over the shared ESI pivot, which supplies the compat date, the ETag/304 cache and
/// the error-limit budget. A character that never granted the scope is refused by the pivot's pre-flight, so no call
/// leaves the machine and no 403 is spent on it. Singleton.
/// </summary>
public sealed class EsiLocationClient(IEsiClient esi) : IEsiLocationClient, ISingletonService
{
    private static readonly IReadOnlyList<string> ReadScopes = [LocationScopeCatalog.ReadLocation];

    public Task<EsiResult<EsiCharacterLocation>> GetLocationAsync(int characterId, CancellationToken cancellationToken = default) =>
        esi.GetAsync<EsiCharacterLocation>($"/characters/{characterId}/location/", characterId, ReadScopes, cancellationToken);
}
