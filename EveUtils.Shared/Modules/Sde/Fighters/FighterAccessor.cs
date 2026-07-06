using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Fighters;

/// <summary>
/// <see cref="IFighterAccessor"/> over the read-only SDE store: builds each <see cref="FighterType"/> from the type's
/// group + dogma attributes, and filters the full fighter catalogue down to a platform's launchable set by its per-kind
/// tube limits. Stateless apart from the injected accessor, so a singleton.
/// </summary>
public sealed class FighterAccessor(ISdeAccessor sde) : IFighterAccessor, ISingletonService
{
    // Fighter inventory groups (category 87): ship fighters and their Standup structure variants. The Standup variants
    // carry no IsLight/IsSupport/IsHeavy flags, so the group is the authoritative kind/structure source.
    private const int LightGroup = 1652, SupportGroup = 1537, HeavyGroup = 1653;
    private const int StructureLightGroup = 4777, StructureSupportGroup = 4778, StructureHeavyGroup = 4779;

    private const int StructureCategory = 65;

    // Fighter attributes.
    private const int SquadronMaxSizeAttr = 2215;   // squadron fighter count
    private const int SquadronRoleAttr = 2270;      // fighterSquadronRole (reload/numShots mapping)
    private const int AttackMultiplierAttr = 2226;  // attack-ability damage multiplier (>0 = the squadron has a weapon)

    // Platform launch tubes: the total (2216) and the per-kind limits (ships only — Upwell structures carry no per-kind
    // limits, just the total).
    private const int TubesAttr = 2216;
    private const int LightSlotsAttr = 2217, SupportSlotsAttr = 2218, HeavySlotsAttr = 2219;

    public FighterType? GetFighterType(int typeId)
    {
        var type = sde.GetType(typeId);
        if (type is null || !TryClassifyKind(type.GroupId, out var kind, out var isStructure))
            return null;

        var attributes = AttributeMap(sde.GetDogmaAttributes(typeId));
        var squadronSize = attributes.TryGetValue(SquadronMaxSizeAttr, out var size) ? (int)size : 0;
        var role = attributes.TryGetValue(SquadronRoleAttr, out var roleValue) ? (FighterRole)(int)roleValue : FighterRole.Unknown;
        var dealsDamage = attributes.TryGetValue(AttackMultiplierAttr, out var multiplier) && multiplier > 0;
        return new FighterType(typeId, type.Name, kind, isStructure, squadronSize, role, type.Volume, dealsDamage);
    }

    public IReadOnlyList<FighterType> ListLaunchableFighters(int platformTypeId)
    {
        var platform = sde.GetType(platformTypeId);
        if (platform is null)
            return [];

        var attributes = AttributeMap(sde.GetDogmaAttributes(platformTypeId));

        // A structure launches the Standup fighters; a ship launches the regular fighters. Match the source so a carrier
        // never offers Standup variants (and vice versa).
        var isStructurePlatform = sde.GetGroup(platform.GroupId)?.CategoryId == StructureCategory;

        // A structure carries no per-kind limits — any Standup kind fills a tube, bounded only by the total (2216). A ship
        // gates each kind on its per-kind limit.
        var hasTubes = attributes.GetValueOrDefault(TubesAttr) > 0;
        var canLaunchLight = isStructurePlatform ? hasTubes : attributes.GetValueOrDefault(LightSlotsAttr) > 0;
        var canLaunchSupport = isStructurePlatform ? hasTubes : attributes.GetValueOrDefault(SupportSlotsAttr) > 0;
        var canLaunchHeavy = isStructurePlatform ? hasTubes : attributes.GetValueOrDefault(HeavySlotsAttr) > 0;
        if (!canLaunchLight && !canLaunchSupport && !canLaunchHeavy)
            return [];

        var launchable = new List<FighterType>();
        foreach (var named in sde.GetFighterTypes())
        {
            if (GetFighterType(named.TypeId) is not { } fighter || fighter.IsStructureFighter != isStructurePlatform)
                continue;
            var allowed = fighter.Kind switch
            {
                FighterKind.Light => canLaunchLight,
                FighterKind.Support => canLaunchSupport,
                FighterKind.Heavy => canLaunchHeavy,
                _ => false
            };
            if (allowed)
                launchable.Add(fighter);
        }

        launchable.Sort((left, right) =>
            left.Kind != right.Kind ? left.Kind.CompareTo(right.Kind) : string.CompareOrdinal(left.Name, right.Name));
        return launchable;
    }

    private static bool TryClassifyKind(int groupId, out FighterKind kind, out bool isStructure)
    {
        switch (groupId)
        {
            case LightGroup: kind = FighterKind.Light; isStructure = false; return true;
            case SupportGroup: kind = FighterKind.Support; isStructure = false; return true;
            case HeavyGroup: kind = FighterKind.Heavy; isStructure = false; return true;
            case StructureLightGroup: kind = FighterKind.Light; isStructure = true; return true;
            case StructureSupportGroup: kind = FighterKind.Support; isStructure = true; return true;
            case StructureHeavyGroup: kind = FighterKind.Heavy; isStructure = true; return true;
            default: kind = default; isStructure = false; return false;
        }
    }

    private static Dictionary<int, double> AttributeMap(IReadOnlyList<SdeDogmaAttribute> attributes)
    {
        var map = new Dictionary<int, double>(attributes.Count);
        foreach (var attribute in attributes)
            map[attribute.AttributeId] = attribute.Value;
        return map;
    }
}
