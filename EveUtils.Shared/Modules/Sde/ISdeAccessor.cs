using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde;

/// <summary>
/// Read-only access to the local SDE store (static EVE reference data). Thread-safe for concurrent reads.
/// Backed by a memory-mapped read-only SQLite file built by the importer; the fit parsers, type-name
/// display and (later) the Dogma engine consume this seam rather than the JSONL directly. After an importer
/// swap, <see cref="Reopen"/> points the accessor at the freshly built file.
/// </summary>
public interface ISdeAccessor
{
    /// <summary>True when a built store exists and can be queried (false before the first import completes).</summary>
    bool IsAvailable { get; }

    /// <summary>The build the store was generated from, or null when no store exists yet.</summary>
    SdeVersion? Version { get; }

    bool TryGetTypeName(int typeId, out string name);

    /// <summary>Case-insensitive name -> typeId (the EFT-import hot path; backed by the lowercased nameKey index).</summary>
    bool TryGetTypeId(string name, out int typeId);

    SdeType? GetType(int typeId);

    IReadOnlyList<SdeDogmaAttribute> GetDogmaAttributes(int typeId);

    /// <summary>Pre-computed slot/hardpoint metadata, or null when the type is not a fittable module.</summary>
    SdeFitRequirement? GetFitRequirement(int typeId);

    SdeSlotType GetSlotType(int typeId);

    /// <summary>The published charge types in a group with their charge size, for the fit-detail charge picker.</summary>
    IReadOnlyList<SdeChargeType> GetChargeTypesInGroup(int groupId);

    /// <summary>The published combat-booster types (those carrying the boosterness attribute 1087), for the fit-detail
    /// booster simulator picker. Ordered by name.</summary>
    IReadOnlyList<SdeNamedType> GetBoosterTypes();

    /// <summary>Every published fighter type (category 87): type id + name, ordered by name. The fighter accessor
    /// resolves each into a <c>FighterType</c> read-model for the launch-tube picker. Empty when the SDE is unavailable.</summary>
    IReadOnlyList<SdeNamedType> GetFighterTypes();

    /// <summary>The curated environment/weather effect beacons (group 920) the fit-simulator weather picker offers
    /// the wormhole class effects, metaliminal storms and Triglavian-invasion effects from the SDE, plus any
    /// synthetic abyssal beacons. Ordered by category then tier. Empty when the SDE is unavailable.</summary>
    IReadOnlyList<SdeEnvironmentBeacon> GetEnvironmentBeacons();

    SdeGroup? GetGroup(int groupId);

    /// <summary>The published groups in a category, ordered by name: the skill-catalogue groups its skills under
    /// their group headers (category 16 = Skill).</summary>
    IReadOnlyList<SdeGroup> GetGroupsByCategory(int categoryId);

    SdeCategory? GetCategory(int categoryId);

    /// <summary>Searches NPC entities (category 11) whose name contains <paramref name="q"/> (case-insensitive),
    /// restricted to types that carry at least one damage attribute (114/116/117/118). Returns at most 50 results
    /// ordered by name. Empty query returns an empty list.</summary>
    IReadOnlyList<NpcEnemy> SearchNpcEnemies(string q);

    /// <summary>Builds a normalised <see cref="DamageProfile"/> from the NPC damage attributes (EM=114, Th=118,
    /// Kin=117, Exp=116) of the given type. Returns null when the type does not exist, is not category 11, or has
    /// no positive damage attributes.</summary>
    DamageProfile? GetNpcDamageProfile(int typeId);

    /// <summary>The site/dungeon catalogue, ordered by name. Every argument is an optional narrowing: a null or blank
    /// <paramref name="nameQuery"/> matches every name (case-insensitive substring), a null
    /// <paramref name="archetypeId"/> or <paramref name="factionId"/> does not filter on that axis. Empty when the
    /// SDE is unavailable.</summary>
    IReadOnlyList<SdeSite> SearchSites(string? nameQuery = null, int? archetypeId = null, int? factionId = null);

    /// <summary>Exact-name site lookup across every SDE locale (ET-79 AC-4): a name copied from the scan window
    /// carries whatever language the client runs in, and CCP translates dungeon names (unlike the signature id).
    /// Matches the normalised name key against the English name and every locale alias. Multiple dungeons can share
    /// a name (218 of 1014 English names do), so callers must not assume a single result and must not pick one.
    /// Distinct from <see cref="SearchSites"/>, which is a substring search for a browser and has different needs.
    /// Empty when the SDE is unavailable or <paramref name="name"/> is blank.</summary>
    IReadOnlyList<SdeSite> FindSitesByExactName(string name);

    /// <summary>Looks up a single agent by id, with its solar system resolved through its station. Null when the
    /// SDE is unavailable or no agent carries that id.</summary>
    SdeAgent? GetAgent(int agentId);

    /// <summary>Exact-name agent lookup across every SDE locale (ET-173 AC-4), the same TypeNameAlias route as
    /// <see cref="TryGetTypeId"/>: the canonical English name first, then a locale alias. Agent names are globally
    /// unique (10.966 agents, 10.966 distinct English names, zero ambiguity — measured, build 3492266), so this
    /// returns at most one agent rather than a list. Null when the SDE is unavailable, <paramref name="name"/> is
    /// blank, or no agent matches.</summary>
    SdeAgent? FindAgentByName(string name);

    /// <summary>Looks up a single mission by id, with its epic arc (if any) resolved. Null when the SDE is
    /// unavailable or no mission carries that id.</summary>
    SdeMission? GetMission(int missionId);

    /// <summary>Exact-name solar system lookup (ET-127): the SDE's own <c>SolarSystem</c> table already carries
    /// name and security for all 8490 systems, so an escalation's typed destination resolves to an id and a
    /// security status without ever asking ESI. Case-insensitive; trims the input. Null when the SDE is
    /// unavailable, <paramref name="name"/> is blank, or no system carries that name.</summary>
    SdeSolarSystem? FindSolarSystemByName(string name);

    /// <summary>Release the store file (drop pooled connections + stop serving queries) so the importer can overwrite
    /// it during the atomic swap — on Windows an open/pooled handle blocks <c>File.Move</c>. Pair with <see cref="Reopen"/>.</summary>
    void Close();

    /// <summary>Re-point the accessor at the store file (call after the importer swaps in a new build).</summary>
    void Reopen();
}
