using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Implants;
using EveUtils.Shared.Modules.Implants.Repositories;

namespace EveUtils.Client.Implants;

/// <summary>
/// Imports a character's plugged-in implants from ESI and stores their type ids. The implants endpoint
/// (<c>/characters/{id}/implants/</c>) returns a flat array of type ids; a re-import replaces the stored set. On a
/// missing scope the character must re-authorize to grant <c>esi-clones.read_implants.v1</c>.
/// </summary>
public sealed class EsiImplantImporter(IEsiClient esi, ICharacterImplantRepository repository) : IEsiImplantImporter
{
    // Imports for one character must not overlap. The background refresh (timer + the RegistryChanged fire-and-forget)
    // and the on-demand fit-detail import can call ImportAsync for the same character concurrently; the
    // ReplaceForCharacterAsync delete-then-insert otherwise races into "UNIQUE constraint failed: CharacterImplant.*"
    // (one call inserts between the other's delete and insert) — the import then throws and the implants are lost,
    // which is why a freshly added character showed no implants until a re-auth. Serialise per character — different
    // characters still import concurrently. Mirrors EsiSkillImporter (922b292). Static so the gate is shared across
    // every injected importer instance and caller.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _importGates = new();

    public event Action<int, IReadOnlyList<int>>? ImplantsChanged;

    public async Task<ImplantImportResult> ImportAsync(int characterId, CancellationToken cancellationToken = default)
    {
        var gate = _importGates.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var implants = await esi.GetAsync<int[]>($"/characters/{characterId}/implants/",
                characterId, [ImplantsScopeCatalog.ReadImplants], cancellationToken);
            if (!implants.IsSuccess || implants.Value is null)
                return Failure(implants.Error);

            await repository.ReplaceForCharacterAsync(characterId, implants.Value, cancellationToken);
            // Notify the UI so the overview implant badge refreshes live, without waiting for the next list rebuild
            // (re-auth/restart): a freshly added character imports its implants in the background after the row is
            // already built. Mirrors CharacterInfoService.AffiliationChanged.
            ImplantsChanged?.Invoke(characterId, implants.Value);
            return ImplantImportResult.Ok(implants.Value.Length);
        }
        finally
        {
            gate.Release();
        }
    }

    private static ImplantImportResult Failure(EsiError? error) => error?.Kind switch
    {
        EsiErrorKind.ScopeMissing => new ImplantImportResult(ImplantImportStatus.ScopeMissing, 0,
            "Implant scope not granted — re-authorize the character to import implants."),
        EsiErrorKind.AuthRequired => new ImplantImportResult(ImplantImportStatus.AuthRequired, 0,
            "Character must re-authenticate."),
        _ => new ImplantImportResult(ImplantImportStatus.Failed, 0, error?.Message ?? "Implant import failed.")
    };
}
