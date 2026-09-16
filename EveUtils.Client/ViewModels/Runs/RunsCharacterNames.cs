using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// What <see cref="RunsOverviewViewModel"/> and everything it hands its own <c>nameOf</c> delegate to (the pane's
/// CREW table, the list row's crew line, the ET-162 detail screen and its FLEET/BOUNTY/MINING/MISSION sections) read
/// a character's name from (ET-306). This machine's own registered characters first, then whatever
/// <c>CachedExternalCharacter</c> already knows or can be made to know through <see cref="IExternalCharacterLookup"/>
/// — the same cache-then-ESI path <c>AddExternalMemberWindow</c> and the fleet roster already use — and only the bare
/// id once neither has anything.
///
/// A name learned once is kept for the rest of this screen's life: <see cref="HydrateAsync"/> is the only thing that
/// touches ESI or the cache, called once per activity read with the ids that read actually needs, never per row per
/// tick (ET-287). <see cref="NameOf"/> itself is a synchronous dictionary read, safe to call from the UI thread or
/// off it.
/// </summary>
internal sealed class RunsCharacterNames(IReadOnlyDictionary<long, string> ownNames, IExternalCharacterLookup? lookup)
{
    private readonly ConcurrentDictionary<long, string> _external = new();

    public string NameOf(long characterId) =>
        ownNames.TryGetValue(characterId, out string? own) ? own
        : _external.TryGetValue(characterId, out string? external) ? external
        : $"character {characterId}";

    /// <summary>Resolves whatever <paramref name="characterIds"/> this screen does not already have a name for —
    /// own characters and ids already learned this session are skipped without touching the cache or ESI at all.
    /// Meant to be awaited inside the same off-UI-thread read that is about to build rows from these ids, so a row
    /// is only ever constructed once its name is settled — never a bare id that later has to be swapped in.</summary>
    public async Task HydrateAsync(IEnumerable<long> characterIds, CancellationToken cancellationToken = default)
    {
        if (lookup is null)
            return;

        long[] missing = [.. characterIds.Distinct()
            .Where(id => id is > 0 and <= int.MaxValue && !ownNames.ContainsKey(id) && !_external.ContainsKey(id))];
        foreach (long characterId in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExternalCharacterInfo info = await lookup.LookupAsync((int)characterId, cancellationToken);
            if (info.Exists && !string.IsNullOrWhiteSpace(info.Name))
                _external[characterId] = info.Name;
        }
    }
}
