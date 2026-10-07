namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>
/// DDL for the read-only SDE store. Tables are created empty, bulk-loaded in one transaction, then indexed
/// (CREATE INDEX after the inserts is far cheaper than maintaining indexes per row). The store holds only the
/// minimal subset we use (data-minimalisation): types/groups/categories, dogma attributes/effects and
/// per-type dogma, a pre-computed slot/hardpoint table for the fit parsers, the site catalogue and the universe map
/// (regions, constellations, systems with 2D position, stargate connections, and each system's celestials). Heavy datasets (typeMaterials,
/// blueprints) are skipped entirely.
/// </summary>
public static class SdeSchema
{
    public const string MetaBuildNumber = "buildNumber";
    public const string MetaReleaseDate = "releaseDate";
    public const string MetaSchemaVersion = "schemaVersion";

    /// <summary>
    /// Bumped whenever the table shape changes so a store built by an older app version is rebuilt on next launch
    /// (the build number alone would not change). v2 added <c>DogmaAttribute.maxAttributeId</c> (attribute capping);
    /// v3 added the <c>TypeNameAlias</c> table for locale-agnostic name import; v4 added the <c>Site</c> table
    /// (the dungeon/site catalogue); v5 added the <c>SiteNameAlias</c> table so a site name copied from a
    /// non-English client resolves too (ET-79 AC-4); v6 added <c>SolarSystem</c>, <c>Agent</c>,
    /// <c>AgentNameAlias</c>, <c>Mission</c> and <c>EpicArcMission</c> — the mission side of the SDE (ET-173);
    /// v7 added <c>Type.metaGroupId</c> (mutated-type detection, ET-146 deel A) and
    /// <c>MutaplasmidAttributeRange</c>/<c>MutaplasmidResultingType</c> (dynamicItemAttributes.jsonl, ET-146 deel D);
    /// v8 added <c>Site.gameplayDescription</c> and the <c>Site.includedTypeIdsJson</c>/<c>excludedTypeIdsJson</c>
    /// pair (ET-232) — the individual-hull refinement <c>shipGroupIdsJson</c> alone could not express;
    /// v9 added <c>SolarSystem.regionId</c> and the <c>Region</c>, <c>NpcCorporation</c> and <c>Faction</c> tables
    /// (ET-335) so a killmail's system, region, attacker corporation and faction resolve from the SDE instead of ESI;
    /// v10 added <c>Type.description</c> for the skill catalogue (ET-351);
    /// v11 added the <c>Constellation</c> and <c>Jump</c> tables, <c>SolarSystem.constellationId/x2d/y2d</c> and
    /// <c>Region.factionId</c> for the world map (ET-391);
    /// v12 added the <c>Celestial</c> and <c>StationOperation</c> tables for a killmail's system map (ET-473).
    /// </summary>
    public const int SchemaVersion = 12;

    /// <summary>Schema-creating statements, run before the bulk load.</summary>
    public static readonly string[] CreateTables =
    [
        "CREATE TABLE Meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;",
        """
        CREATE TABLE Type (
            typeId        INTEGER PRIMARY KEY,
            groupId       INTEGER NOT NULL,
            nameEn        TEXT NOT NULL,
            description   TEXT,
            nameKey       TEXT NOT NULL,
            published     INTEGER NOT NULL,
            mass          REAL NOT NULL,
            volume        REAL NOT NULL,
            capacity      REAL NOT NULL,
            marketGroupId INTEGER,
            metaGroupId   INTEGER
        ) WITHOUT ROWID;
        """,
        """
        CREATE TABLE InvGroup (
            groupId    INTEGER PRIMARY KEY,
            categoryId INTEGER NOT NULL,
            nameEn     TEXT NOT NULL,
            published  INTEGER NOT NULL
        ) WITHOUT ROWID;
        """,
        """
        CREATE TABLE Category (
            categoryId INTEGER PRIMARY KEY,
            nameEn     TEXT NOT NULL,
            published  INTEGER NOT NULL
        ) WITHOUT ROWID;
        """,
        """
        CREATE TABLE DogmaAttribute (
            attributeId      INTEGER PRIMARY KEY,
            name             TEXT NOT NULL,
            displayNameEn    TEXT,
            defaultValue     REAL NOT NULL,
            stackable        INTEGER NOT NULL,
            highIsGood       INTEGER NOT NULL,
            unitId           INTEGER,
            published        INTEGER NOT NULL,
            maxAttributeId   INTEGER
        ) WITHOUT ROWID;
        """,
        // modifierInfoJson preserves the raw modifier array verbatim for the Dogma engine without
        // committing to a modifier schema now.
        """
        CREATE TABLE DogmaEffect (
            effectId         INTEGER PRIMARY KEY,
            name             TEXT NOT NULL,
            effectCategoryId INTEGER NOT NULL,
            published        INTEGER NOT NULL,
            modifierInfoJson TEXT
        ) WITHOUT ROWID;
        """,
        "CREATE TABLE TypeDogmaAttribute (typeId INTEGER NOT NULL, attributeId INTEGER NOT NULL, value REAL NOT NULL);",
        "CREATE TABLE TypeDogmaEffect (typeId INTEGER NOT NULL, effectId INTEGER NOT NULL, isDefault INTEGER NOT NULL);",
        """
        CREATE TABLE TypeFitRequirement (
            typeId        INTEGER PRIMARY KEY,
            slotType      INTEGER NOT NULL,
            numberOfSlots INTEGER NOT NULL,
            isLauncher    INTEGER NOT NULL,
            isTurret      INTEGER NOT NULL
        ) WITHOUT ROWID;
        """,
        // Locale-agnostic name import: one row per non-English type name so a German/French/… EFT-fit
        // resolves to the same typeId. The canonical English name stays on Type.nameKey; display/export read
        // Type.nameEn and are unaffected. Multiple rows per typeId (one per locale) → no WITHOUT ROWID.
        "CREATE TABLE TypeNameAlias (typeId INTEGER NOT NULL, nameKey TEXT NOT NULL, locale TEXT NOT NULL);",
        // The site/dungeon catalogue. archetypeName and factionName are denormalised at build time (34 archetypes,
        // 27 factions are too small to earn their own tables and joins). Everything but the id and the name is
        // nullable because the empty case is the normal one: 1183 of 1409 sites carry no description, 77 no faction,
        // 45 no archetype title, 962 no ship restriction. shipGroupIdsJson distinguishes "no restriction" (NULL)
        // from "restricted" (a JSON array of InvGroup ids, possibly empty — see TableWriters). gameplayDescription
        // (ET-232) is a second, separate text field from the SDE's own gameplayDescription — recommended fleet size,
        // expected time, roles — never merged into description, which is CCP's own flavour text. includedTypeIdsJson
        // and excludedTypeIdsJson (ET-232) carry the individual-hull refinement the group-only shipGroupIdsJson
        // cannot express (a homefront's "T1 cruisers only" is 16 specific types, not a group), NULL vs "[]" kept
        // distinct the same way.
        """
        CREATE TABLE Site (
            dungeonId          INTEGER PRIMARY KEY,
            nameEn             TEXT NOT NULL,
            archetypeId        INTEGER,
            archetypeName      TEXT,
            factionId          INTEGER,
            factionName        TEXT,
            description        TEXT,
            gameplayDescription TEXT,
            dedRating          INTEGER,
            shipGroupIdsJson   TEXT,
            includedTypeIdsJson TEXT,
            excludedTypeIdsJson TEXT
        ) WITHOUT ROWID;
        """,
        // Locale-agnostic name import for sites, same idea as TypeNameAlias but carrying English too (locale "en"):
        // Site has no persisted nameKey column of its own, so the lookup goes through this table for every locale
        // rather than mixing it with an ASCII-only SQL LOWER() over Site.nameEn — see TableWriters and
        // SqliteSdeAccessor.FindSitesByExactName.
        "CREATE TABLE SiteNameAlias (dungeonId INTEGER NOT NULL, nameKey TEXT NOT NULL, locale TEXT NOT NULL);",
        // The mission side of the SDE (ET-173). SolarSystem backs Agent.solarSystemId; agent and site name
        // resolution is only ever by id, never joined against Site's own dungeonId space (see Mission below).
        // regionId (ET-335) comes straight off mapSolarSystems.jsonl, as do constellationId and the schematic 2D
        // position (ET-391). x2d/y2d are CCP's position2D with y negated: the SDE has y+ = north, the map draws
        // y+ = down, so consumers use them as screen coordinates as-is. Both are NULL for systems without a
        // position2D — wormhole and abyssal space, which also have no gates.
        """
        CREATE TABLE SolarSystem (
            solarSystemId  INTEGER PRIMARY KEY,
            nameEn         TEXT NOT NULL,
            securityStatus REAL NOT NULL,
            regionId       INTEGER NOT NULL,
            constellationId INTEGER NOT NULL,
            x2d            REAL,
            y2d            REAL
        ) WITHOUT ROWID;
        """,
        // Only npcCharacters rows with an `agent` sub-object become a row here (ET-173 AC-2). solarSystemId is
        // resolved at import time from npcStations.jsonl (agent -> station -> system) and is null when that
        // dataset is unavailable — nothing here depends on Site or dungeonId. agentTypeName is denormalised from
        // agentTypes.jsonl (13 rows, too small for its own table, same reasoning as Site.archetypeName).
        """
        CREATE TABLE Agent (
            agentId       INTEGER PRIMARY KEY,
            nameEn        TEXT NOT NULL,
            nameKey       TEXT NOT NULL,
            level         INTEGER NOT NULL,
            agentTypeId   INTEGER NOT NULL,
            agentTypeName TEXT,
            divisionId    INTEGER NOT NULL,
            isLocator     INTEGER NOT NULL,
            corporationId INTEGER NOT NULL,
            locationId    INTEGER NOT NULL,
            solarSystemId INTEGER
        ) WITHOUT ROWID;
        """,
        // Locale-agnostic name import for agents, the TypeNameAlias route (ET-173 AC-4): one row per non-English
        // locale, English stays canonical on Agent.nameKey. Not the SiteNameAlias route — agent names carry no
        // known non-ASCII-English edge case, so there is no reason to route "en" through here too.
        "CREATE TABLE AgentNameAlias (agentId INTEGER NOT NULL, nameKey TEXT NOT NULL, locale TEXT NOT NULL);",
        // Name and keys only — the eight-language message/reward blocks are the bulk of missions.jsonl's 53 MB raw
        // and are not imported. killMissionDungeonId is its own id space: 1.460 distinct ids against Site's 1.409,
        // the same numeric range, with 3 accidental overlaps (13341, 13342, 14100 — ET-173 AC-5). It must never be
        // compared against Site.dungeonId as if the two were the same catalogue.
        """
        CREATE TABLE Mission (
            missionId            INTEGER PRIMARY KEY,
            nameEn               TEXT NOT NULL,
            agentTypeId          INTEGER,
            killMissionDungeonId INTEGER
        ) WITHOUT ROWID;
        """,
        // missionId -> arcId only (ET-173 AC-6, minimal by design); the nextMissions chain graph is a read
        // concern (ET-131), not an import concern.
        "CREATE TABLE EpicArcMission (missionId INTEGER PRIMARY KEY, arcId INTEGER NOT NULL) WITHOUT ROWID;",
        // Id, English name and owning faction (ET-335, ET-391), resolved through SolarSystem.regionId above.
        "CREATE TABLE Region (regionId INTEGER PRIMARY KEY, nameEn TEXT NOT NULL, factionId INTEGER) WITHOUT ROWID;",
        "CREATE TABLE Constellation (constellationId INTEGER PRIMARY KEY, nameEn TEXT NOT NULL, regionId INTEGER NOT NULL, factionId INTEGER) WITHOUT ROWID;",
        // Stargate connections (ET-391). mapStargates.jsonl lists every gate once per side (13978 gates = 6989
        // connections); the importer stores one row per connection with fromSystemId < toSystemId. A reader that
        // wants both directions mirrors the row itself — the primary key serves lookups by the lower id, the
        // IX_Jump_toSystemId index those by the higher id.
        "CREATE TABLE Jump (fromSystemId INTEGER NOT NULL, toSystemId INTEGER NOT NULL, PRIMARY KEY (fromSystemId, toSystemId)) WITHOUT ROWID;",
        // Everything a killmail's system map draws (ET-473): one row per sun, planet, moon, asteroid belt, stargate and
        // NPC station, keyed by system first so one system's rows sit together and need no extra index. kind is
        // CelestialKind. x/y/z are metres from the sun, which mapStars.jsonl gives no position because it is the
        // origin. Names are not stored — the reader builds them like the game does (verified against ESI for 391
        // objects): a planet is "<system> <celestialIndex as roman>", a moon or belt "<orbit> - Moon|Asteroid Belt
        // <orbitIndex>", a gate "Stargate (<destination>)", a station "<orbit> - <owner>[ <operation>]".
        // operationId is null when the station's name leaves the operation out (useOperationName false).
        """
        CREATE TABLE Celestial (
            solarSystemId       INTEGER NOT NULL,
            itemId              INTEGER NOT NULL,
            kind                INTEGER NOT NULL,
            orbitId             INTEGER,
            celestialIndex      INTEGER,
            orbitIndex          INTEGER,
            x                   REAL NOT NULL,
            y                   REAL NOT NULL,
            z                   REAL NOT NULL,
            destinationSystemId INTEGER,
            ownerId             INTEGER,
            operationId         INTEGER,
            PRIMARY KEY (solarSystemId, itemId)
        ) WITHOUT ROWID;
        """,
        // The operation half of an NPC station's name ("Bureau", "Retail Center"), from stationOperations.jsonl.
        "CREATE TABLE StationOperation (operationId INTEGER PRIMARY KEY, nameEn TEXT NOT NULL) WITHOUT ROWID;",
        // Id + English name only (ET-335) — a killmail's attacker/victim corporation or faction id resolves here
        // when it belongs to an NPC; a miss means the id is a player's and must go to ESI instead (see
        // ISdeAccessor.GetNpcCorporationName/GetFactionName).
        "CREATE TABLE NpcCorporation (corporationId INTEGER PRIMARY KEY, nameEn TEXT NOT NULL) WITHOUT ROWID;",
        "CREATE TABLE Faction (factionId INTEGER PRIMARY KEY, nameEn TEXT NOT NULL) WITHOUT ROWID;",
        // dynamicItemAttributes.jsonl (ET-146 deel D): one row per (mutaplasmid, rollable attribute). The min/max
        // are multipliers on the source type's base value, not rolled values themselves — see the ticket's
        // research. No consumer reads this yet (deel B decides how the unknown-state should use it).
        "CREATE TABLE MutaplasmidAttributeRange (mutaplasmidTypeId INTEGER NOT NULL, attributeId INTEGER NOT NULL, min REAL NOT NULL, max REAL NOT NULL);",
        // The same dataset's inputOutputMapping: which source types (applicableTypeId) a mutaplasmid can roll,
        // and which resulting type each produces. Not used by deel A's detection (that runs on metaGroupId
        // alone) — this is the join deel B needs the other way round: a fit only carries the resulting type
        // (e.g. 47408), so resultingTypeId -> mutaplasmidTypeId here is the step before
        // GetMutaplasmidAttributeRanges(mutaplasmidTypeId) can report a range for that module instead of only
        // "unknown".
        "CREATE TABLE MutaplasmidResultingType (mutaplasmidTypeId INTEGER NOT NULL, applicableTypeId INTEGER NOT NULL, resultingTypeId INTEGER NOT NULL);"
    ];

    /// <summary>Index-creating statements, run after the bulk load.</summary>
    public static readonly string[] CreateIndexes =
    [
        // The hot path: case-insensitive name -> typeId for EFT import (lowercased nameKey, O(log n)).
        "CREATE INDEX IX_Type_nameKey ON Type (nameKey);",
        "CREATE INDEX IX_TypeNameAlias_nameKey ON TypeNameAlias (nameKey);",
        "CREATE INDEX IX_SiteNameAlias_nameKey ON SiteNameAlias (nameKey);",
        "CREATE INDEX IX_Type_groupId ON Type (groupId);",
        "CREATE INDEX IX_TypeDogmaAttribute_typeId ON TypeDogmaAttribute (typeId);",
        "CREATE INDEX IX_TypeDogmaEffect_typeId ON TypeDogmaEffect (typeId);",
        // The two site filter axes. Name search is a substring LIKE, which no index can serve.
        "CREATE INDEX IX_Site_archetypeId ON Site (archetypeId);",
        "CREATE INDEX IX_Site_factionId ON Site (factionId);",
        "CREATE INDEX IX_Jump_toSystemId ON Jump (toSystemId);",
        "CREATE INDEX IX_Agent_nameKey ON Agent (nameKey);",
        "CREATE INDEX IX_AgentNameAlias_nameKey ON AgentNameAlias (nameKey);"
    ];
}
