namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>
/// DDL for the read-only SDE store. Tables are created empty, bulk-loaded in one transaction, then indexed
/// (CREATE INDEX after the inserts is far cheaper than maintaining indexes per row). The store holds only the
/// minimal subset we use (data-minimalisation): types/groups/categories, dogma attributes/effects and
/// per-type dogma, a pre-computed slot/hardpoint table for the fit parsers and the site catalogue. Heavy datasets (map*,
/// typeMaterials, blueprints) are skipped entirely.
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
    /// (the dungeon/site catalogue).
    /// </summary>
    public const int SchemaVersion = 4;

    /// <summary>Schema-creating statements, run before the bulk load.</summary>
    public static readonly string[] CreateTables =
    [
        "CREATE TABLE Meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;",
        """
        CREATE TABLE Type (
            typeId        INTEGER PRIMARY KEY,
            groupId       INTEGER NOT NULL,
            nameEn        TEXT NOT NULL,
            nameKey       TEXT NOT NULL,
            published     INTEGER NOT NULL,
            mass          REAL NOT NULL,
            volume        REAL NOT NULL,
            capacity      REAL NOT NULL,
            marketGroupId INTEGER
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
        // from "restricted" (a JSON array of InvGroup ids, possibly empty — see TableWriters).
        """
        CREATE TABLE Site (
            dungeonId        INTEGER PRIMARY KEY,
            nameEn           TEXT NOT NULL,
            archetypeId      INTEGER,
            archetypeName    TEXT,
            factionId        INTEGER,
            factionName      TEXT,
            description      TEXT,
            dedRating        INTEGER,
            shipGroupIdsJson TEXT
        ) WITHOUT ROWID;
        """
    ];

    /// <summary>Index-creating statements, run after the bulk load.</summary>
    public static readonly string[] CreateIndexes =
    [
        // The hot path: case-insensitive name -> typeId for EFT import (lowercased nameKey, O(log n)).
        "CREATE INDEX IX_Type_nameKey ON Type (nameKey);",
        "CREATE INDEX IX_TypeNameAlias_nameKey ON TypeNameAlias (nameKey);",
        "CREATE INDEX IX_Type_groupId ON Type (groupId);",
        "CREATE INDEX IX_TypeDogmaAttribute_typeId ON TypeDogmaAttribute (typeId);",
        "CREATE INDEX IX_TypeDogmaEffect_typeId ON TypeDogmaEffect (typeId);",
        // The two site filter axes. Name search is a substring LIKE, which no index can serve.
        "CREATE INDEX IX_Site_archetypeId ON Site (archetypeId);",
        "CREATE INDEX IX_Site_factionId ON Site (factionId);"
    ];
}
