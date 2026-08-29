using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.Esi;

/// <summary>Reads a character's own solar system. Split out so the abyssal monitor can be tested without ESI.</summary>
public interface IEsiLocationClient
{
    Task<EsiResult<EsiCharacterLocation>> GetLocationAsync(int characterId, CancellationToken cancellationToken = default);
}
