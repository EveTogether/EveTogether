using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Repositories;

namespace EveUtils.Client.Skills;

/// <summary>
/// Imports a character's skills from ESI and stores the effective levels. Loads the skills snapshot
/// (<c>/characters/{id}/skills/</c>) and the skill-queue (<c>/characters/{id}/skillqueue/</c>); because the snapshot
/// lags behind the last in-game session, any queue entry already finished counts as trained (see
/// <see cref="SkillLevelMerge"/>). It also stores the raw queue and the character's training attributes
/// (<c>/characters/{id}/attributes/</c>, same read_skills scope) for the read-only skill-queue view and the SP/min
/// rate. On a missing scope the character must re-authorize to grant the two skill scopes.
/// </summary>
public sealed class EsiSkillImporter(
    IEsiClient esi,
    ICharacterSkillRepository repository,
    ICharacterSkillQueueRepository queueRepository,
    ICharacterAttributesRepository attributesRepository) : IEsiSkillImporter
{
    // Imports for one character must not overlap. The background refresh (timer + the RegistryChanged fire-and-forget)
    // and the on-demand fit-detail import can call ImportAsync for the same character concurrently; each
    // ReplaceForCharacterAsync does a delete-then-insert that otherwise races into a
    // "UNIQUE constraint failed: CharacterSkill.CharacterId, CharacterSkill.SkillTypeId" (one call inserts between the
    // other's delete and insert). Serialise per character — different characters still import concurrently. Static so
    // the gate is shared across every injected importer instance and caller.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _importGates = new();

    public async Task<SkillImportResult> ImportAsync(int characterId, CancellationToken cancellationToken = default)
    {
        var gate = _importGates.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var skills = await esi.GetAsync<EsiCharacterSkills>($"/characters/{characterId}/skills/",
                characterId, [SkillsScopeCatalog.ReadSkills], cancellationToken);
            if (!skills.IsSuccess || skills.Value is null)
                return Failure(skills.Error);

            var queue = await esi.GetAsync<EsiSkillQueueEntry[]>($"/characters/{characterId}/skillqueue/",
                characterId, [SkillsScopeCatalog.ReadSkillQueue], cancellationToken);
            // The queue is best-effort: if it fails we still store the snapshot (better than nothing); only a failed
            // skills call is fatal. The queue only ever raises levels, never lowers them.
            var queueEntries = queue is { IsSuccess: true, Value: not null } ? queue.Value : [];

            var levels = SkillLevelMerge.Effective(skills.Value.Skills, queueEntries, DateTimeOffset.UtcNow);
            await repository.ReplaceForCharacterAsync(characterId, levels, cancellationToken);

            // persist the raw queue (for the read-only queue view) and the training attributes (for the SP/min rate).
            // Both are best-effort — a failure here must not fail the skills import the engine relies on.
            await queueRepository.ReplaceForCharacterAsync(characterId,
                queueEntries.Select(e => _ToEntity(characterId, e)).ToList(), cancellationToken);

            var attributes = await esi.GetAsync<EsiCharacterAttributes>($"/characters/{characterId}/attributes/",
                characterId, [SkillsScopeCatalog.ReadSkills], cancellationToken);
            if (attributes is { IsSuccess: true, Value: not null })
                await attributesRepository.ReplaceForCharacterAsync(_ToEntity(characterId, attributes.Value), cancellationToken);

            return SkillImportResult.Ok(levels.Count);
        }
        finally
        {
            gate.Release();
        }
    }

    private static CharacterSkillQueueEntry _ToEntity(int characterId, EsiSkillQueueEntry entry) => new()
    {
        CharacterId = characterId,
        QueuePosition = entry.QueuePosition,
        SkillTypeId = entry.SkillId,
        FinishedLevel = entry.FinishedLevel,
        StartDate = entry.StartDate,
        FinishDate = entry.FinishDate
    };

    private static CharacterAttributes _ToEntity(int characterId, EsiCharacterAttributes attributes) => new()
    {
        CharacterId = characterId,
        Charisma = attributes.Charisma,
        Intelligence = attributes.Intelligence,
        Memory = attributes.Memory,
        Perception = attributes.Perception,
        Willpower = attributes.Willpower
    };

    private static SkillImportResult Failure(EsiError? error) => error?.Kind switch
    {
        EsiErrorKind.ScopeMissing => new SkillImportResult(SkillImportStatus.ScopeMissing, 0,
            "Skill scopes not granted — re-authorize the character to import skills."),
        EsiErrorKind.AuthRequired => new SkillImportResult(SkillImportStatus.AuthRequired, 0,
            "Character must re-authenticate."),
        _ => new SkillImportResult(SkillImportStatus.Failed, 0, error?.Message ?? "Skill import failed.")
    };
}
