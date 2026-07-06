using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Client.Skills;

/// <summary>Imports a character's effective skill levels from ESI (skills snapshot + skill-queue) and caches them.</summary>
public interface IEsiSkillImporter
{
    Task<SkillImportResult> ImportAsync(int characterId, CancellationToken cancellationToken = default);
}
