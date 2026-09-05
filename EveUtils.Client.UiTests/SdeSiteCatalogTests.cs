using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Sde.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The SDE import builds a site catalogue from dungeons.jsonl (ET-36), end-to-end through the real
/// <see cref="SdeSqliteBuilder"/> + <see cref="SqliteSdeAccessor"/> against an in-test zip, no network.
/// </summary>
public sealed class SdeSiteCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sde-sites-{Guid.NewGuid():N}");

    // Shapes copied from build 3484357: name/description/title are locale objects, allowedShipsList is an array of
    // typeList ids, and a typeList carries includedGroupIDs of real InvGroup ids.
    private static readonly Dictionary<string, string[]> Catalog = new()
    {
        ["groups.jsonl"] =
        [
            """{"_key":25,"categoryID":6,"name":{"en":"Frigate"},"published":true}""",
            """{"_key":31,"categoryID":6,"name":{"en":"Shuttle"},"published":true}""",
            """{"_key":237,"categoryID":6,"name":{"en":"Corvette"},"published":true}"""
        ],
        ["archetypes.jsonl"] =
        [
            """{"_key":24,"title":{"en":"Combat Sites","de":"Kampfgebiete"},"description":{"en":"Combat."}}""",
            // Archetype 43 exists but has no title at all — 45 live dungeons sit on it.
            """{"_key":43,"description":{"en":"Seasonal event sites."}}"""
        ],
        ["factions.jsonl"] =
        [
            """{"_key":500010,"name":{"en":"Guristas Pirates","de":"Guristas-Piraten"},"corporationID":1000127}"""
        ],
        ["typeLists.jsonl"] =
        [
            """{"_key":482,"includedGroupIDs":[25,31],"name":"Dungeon Ship Restrictions [111]"}""",
            """{"_key":478,"includedGroupIDs":[237],"name":"Dungeon Ship Restrictions [116]"}""",
            // A restriction expressed per hull instead of per group: resolves to no ship groups at all.
            """{"_key":453,"includedTypeIDs":[621,630],"name":"Dungeon Ship Restrictions [hulls]"}"""
        ],
        ["dungeons.jsonl"] =
        [
            """{"_key":43,"name":{"en":"Guristas Supply Depot","de":"Guristas-Versorgungsdepot"},"description":{"en":"<P>A supply depot.</P>\n<P>DED Threat Assessment: Minor (2 of 10)</P>","de":"<P>Ein Depot.</P>"},"archetypeID":24,"factionID":500010,"allowedShipsList":[482,478]}""",
            """{"_key":100,"name":{"en":"Seasonal Event Pocket"},"archetypeID":43}""",
            """{"_key":200,"name":{"en":"Unrated Complex"},"description":{"en":"<P>Guarded.</P><BR><P>DED Threat Assessment: pending.</P>"},"archetypeID":24,"factionID":500010}""",
            """{"_key":300,"name":{"en":"Hull Restricted Pocket"},"archetypeID":24,"allowedShipsList":[453]}"""
        ]
    };

    private SqliteSdeAccessor? _catalog;

    /// <summary>The catalogue store, built once per test (the builder deletes its output, so it cannot be rebuilt
    /// while an accessor still holds the file).</summary>
    private SqliteSdeAccessor Sde => _catalog ??= BuildStore(Catalog);

    private SqliteSdeAccessor BuildStore(Dictionary<string, string[]> datasets)
    {
        Directory.CreateDirectory(_dir);
        var zipPath = Path.Combine(_dir, "sde.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach (var (name, lines) in datasets)
            {
                using var entry = new StreamWriter(zip.CreateEntry(name).Open());
                foreach (var line in lines)
                    entry.WriteLine(line);
            }

        var dbPath = Path.Combine(_dir, "sde.db");
        new SdeSqliteBuilder().Build(zipPath, dbPath,
            new SdeVersion(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            progress: null, TestContext.Current.CancellationToken);
        return new SqliteSdeAccessor(dbPath);
    }

    private SdeSite Site(int dungeonId) => Assert.Single(Sde.SearchSites(), s => s.DungeonId == dungeonId);

    [Fact]
    public void Import_ResolvesArchetypeAndFaction_AndPicksTheEnglishName()
    {
        var site = Site(43);

        Assert.Equal("Guristas Supply Depot", site.Name);          // en, not de
        Assert.Equal(24, site.ArchetypeId);
        Assert.Equal("Combat Sites", site.ArchetypeName);
        Assert.Equal(500010, site.FactionId);
        Assert.Equal("Guristas Pirates", site.FactionName);
    }

    [Fact]
    public void Import_StripsHtmlFromTheDescription_Once()
    {
        var site = Site(43);

        Assert.Equal("A supply depot. DED Threat Assessment: Minor (2 of 10)", site.Description);
        Assert.DoesNotContain('<', site.Description!);
    }

    [Fact]
    public void Import_ReadsTheDedRating_OnlyWhereTheDescriptionStatesOne()
    {
        Assert.Equal(2, Site(43).DedRating);
        // The phrase without a parsable "(N of 10)" leaves the rating absent — not 0, not "unknown".
        Assert.Null(Site(200).DedRating);
    }

    [Fact]
    public void Import_LeavesTheEmptyCasesEmpty_AndStillSortsAndFilters()
    {
        var site = Site(100);

        Assert.Null(site.Description);      // 1183 of 1409 sites have none
        Assert.Null(site.FactionId);        // 77 have none
        Assert.Null(site.FactionName);
        Assert.Equal(43, site.ArchetypeId); // archetype 43 has no title in the SDE
        Assert.Null(site.ArchetypeName);

        // A titleless archetype must not fall out of a filter or break the ordering.
        Assert.Equal([100], Sde.SearchSites(archetypeId: 43).Select(s => s.DungeonId));
        var names = Sde.SearchSites().Select(s => s.Name).ToList();
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public void Import_ResolvesAllowedShipsList_IntoAnAllowListOfShipGroups()
    {
        var site = Site(43);

        Assert.True(site.IsShipRestricted);
        // Both referenced type lists contribute: 482 -> Frigate + Shuttle, 478 -> Corvette.
        Assert.Equal(["Corvette", "Frigate", "Shuttle"], site.AllowedShipGroups.Select(g => g.Name).Order());
    }

    [Fact]
    public void Import_SiteWithoutRestriction_IsNotRestricted()
    {
        var site = Site(100);

        Assert.False(site.IsShipRestricted);
        Assert.Empty(site.AllowedShipGroups);
    }

    [Fact]
    public void Import_RestrictionThatIsNotExpressibleAsGroups_StaysRestricted()
    {
        // Type list 453 restricts per hull, so no ship group comes out — but the site is still restricted and must
        // not read as "all ships allowed".
        var site = Site(300);

        Assert.True(site.IsShipRestricted);
        Assert.Empty(site.AllowedShipGroups);
    }

    [Fact]
    public void SearchSites_FiltersByNameArchetypeAndFaction()
    {
        // Guristas Supply Depot, Hull Restricted Pocket, Seasonal Event Pocket, Unrated Complex.
        Assert.Equal([43, 300, 100, 200], Sde.SearchSites().Select(s => s.DungeonId));
        Assert.Equal([43], Sde.SearchSites("supply depot").Select(s => s.DungeonId));   // case-insensitive substring
        Assert.Equal([43, 200], Sde.SearchSites(factionId: 500010).Select(s => s.DungeonId));
        Assert.Equal([43, 300, 200], Sde.SearchSites(archetypeId: 24).Select(s => s.DungeonId));
        Assert.Equal([43], Sde.SearchSites("depot", archetypeId: 24, factionId: 500010).Select(s => s.DungeonId));
        Assert.Empty(Sde.SearchSites("no such site"));
    }

    [Fact]
    public void SchemaVersion_IsSeven_AndAnOlderStoreReadsAsUnavailable()
    {
        Assert.Equal(7, SdeSchema.SchemaVersion);

        // A store left behind by a v3 build has no Site table. The accessor must refuse it outright so
        // SdeImporter.CheckForUpdateAsync sees a null local version and offers the rebuild.
        Directory.CreateDirectory(_dir);
        var dbPath = Path.Combine(_dir, "v3.db");
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE Meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;" +
                $"INSERT INTO Meta VALUES ('{SdeSchema.MetaSchemaVersion}', '3'), ('{SdeSchema.MetaBuildNumber}', '3386912');";
            command.ExecuteNonQuery();
        }

        var sde = new SqliteSdeAccessor(dbPath);
        Assert.False(sde.IsAvailable);
        Assert.Null(sde.Version);
        Assert.Empty(sde.SearchSites());
    }

    // ET-79 AC-4: a site name copied from a non-English client must resolve to the same dungeon as the English
    // name. Tegenproef: drop the importer's alias writes (or query only nameEn) and this goes red.
    [Fact]
    public void FindSitesByExactName_MatchesEveryLocale_AndReturnsTheSameDungeonAsTheEnglishName()
    {
        Assert.Equal([43], Sde.FindSitesByExactName("Guristas Supply Depot").Select(s => s.DungeonId));
        Assert.Equal([43], Sde.FindSitesByExactName("Guristas-Versorgungsdepot").Select(s => s.DungeonId));
        Assert.Empty(Sde.FindSitesByExactName("Versorgungs"));  // exact, not a substring match
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup of the throwaway store
        }
    }
}
