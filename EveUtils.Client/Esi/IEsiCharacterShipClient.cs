using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.Esi;

/// <summary>Reads a character's active ship without exposing ESI to fit-detection readers.</summary>
public interface IEsiCharacterShipClient
{
    Task<EsiResult<EsiCharacterShip>> GetShipAsync(int characterId, CancellationToken cancellationToken = default);
}
