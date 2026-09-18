using EveUtils.Server.Esi;
using EveUtils.Shared.Modules.ServerAuth.Entities;

namespace EveUtils.Server.DataExplorer;

/// <summary>
/// Names for the character ids a Data page shows: paired characters name themselves, and only the ids nobody paired
/// here go to ESI. A failed lookup leaves the id unnamed rather than failing the page.
/// </summary>
public static class CharacterNames
{
    public static async Task<IReadOnlyDictionary<long, string>> ResolveAsync(
        IEnumerable<SyncedCharacter> paired, IEnumerable<long> shown, EsiNameLookup lookup, CancellationToken ct = default)
    {
        var names = paired.ToDictionary(c => (long)c.EsiCharacterId, c => c.CharacterName);
        foreach (var (id, name) in await lookup.ResolveAsync(shown.Where(id => !names.ContainsKey(id)), ct))
            names[id] = name;
        return names;
    }
}
