using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>An ore resolved by its exact SDE name (ET-229): its type, and whether it is priced at Mutanite's fixed NPC
/// price rather than the market's.</summary>
public sealed record MiningOreType(int TypeId, bool IsMutanite);

/// <summary>
/// Every distinct ore name one read touches, looked up once (ET-290, after ET-298). Each <c>SqliteSdeAccessor</c> call
/// opens a pooled connection and runs a PRAGMA, and a detail read or a summary rebuild used to look the same ore up for
/// every mined line — once to find its type, once more to price it, once more to ask whether it is Mutanite.
/// </summary>
public sealed class MiningOreTypes
{
    private readonly Dictionary<string, MiningOreType?> _byName;

    private MiningOreTypes(Dictionary<string, MiningOreType?> byName) => _byName = byName;

    /// <summary>Nothing resolves while the SDE is unavailable: an ore that cannot be named yet is not priced, the same
    /// "not priced yet, not zero" rule loot follows.</summary>
    public static MiningOreTypes Resolve(IEnumerable<string> oreNames, ISdeAccessor sde)
    {
        var byName = new Dictionary<string, MiningOreType?>(StringComparer.Ordinal);
        foreach (string oreName in oreNames)
        {
            if (byName.ContainsKey(oreName))
                continue;

            byName[oreName] = sde.IsAvailable && sde.TryGetTypeId(oreName, out int typeId)
                ? new MiningOreType(typeId, MiningValuation.IsMutanite(sde, typeId))
                : null;
        }

        return new MiningOreTypes(byName);
    }

    /// <summary>Null for a name this read never resolved, or one the SDE does not know.</summary>
    public MiningOreType? Of(string oreName) => _byName.GetValueOrDefault(oreName);

    public IEnumerable<int> TypeIds => _byName.Values.OfType<MiningOreType>().Select(ore => ore.TypeId).Distinct();
}
