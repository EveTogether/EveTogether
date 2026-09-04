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
/// The SDE import builds the mission side of the store from npcCharacters/npcStations/mapSolarSystems/missions/
/// agentTypes/epicArcs (ET-173) — agents with their level, resolved to a system, missions with their own id space
/// and epic-arc membership. End-to-end through the real <see cref="SdeSqliteBuilder"/> + <see cref="SqliteSdeAccessor"/>
/// against an in-test zip, no network. Shapes copied from build 3492266.
/// </summary>
public sealed class SdeMissionCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sde-mission-{Guid.NewGuid():N}");

    private static readonly Dictionary<string, string[]> FullCatalog = new()
    {
        ["mapSolarSystems.jsonl"] =
        [
            """{"_key":30005040,"name":{"en":"Nishah","de":"Nishah"},"securityStatus":0.6}"""
        ],
        ["npcStations.jsonl"] =
        [
            """{"_key":60008689,"solarSystemID":30005040,"celestialIndex":7,"orbitIndex":5,"ownerID":1000089,"operationID":34}"""
        ],
        ["agentTypes.jsonl"] =
        [
            """{"_key":10,"name":"EpicArcAgent"}""",
            """{"_key":2,"name":"BasicAgent"}"""
        ],
        ["npcCharacters.jsonl"] =
        [
            // The one real capture this project has: Aralin Jick, an EpicArcAgent at level 4 in Nishah.
            """{"_key":3019407,"name":{"en":"Aralin Jick","de":"Aralin Jick DE"},"corporationID":1000089,"locationID":60008689,"agent":{"agentTypeID":10,"divisionID":18,"isLocator":false,"level":4}}""",
            // A generic NPC with no `agent` sub-object — a corporation CEO, not an agent (ET-173 AC-2).
            """{"_key":3000001,"name":{"en":"Some Corp CEO"},"corporationID":1000089,"locationID":60008689}"""
        ],
        ["missions.jsonl"] =
        [
            """{"_key":9001,"name":{"en":"Paragon Requests: Ships for Tips"},"agentTypeID":10,"killMission":{"dungeonID":13341}}"""
        ],
        ["epicArcs.jsonl"] =
        [
            """{"_key":77,"name":{"en":"The Blood-Stained Stars"},"missions":[{"_key":9001,"agentID":3019407}]}"""
        ],
        ["dungeons.jsonl"] =
        [
            // Shares the numeric id 13341 with the mission's killMission.dungeonID above, purely by
            // coincidence (ET-173 AC-5) — an unrelated site, never to be joined against the mission.
            """{"_key":13341,"name":{"en":"Unrelated Site At 13341"}}"""
        ]
    };

    private SqliteSdeAccessor? _sde;
    private SqliteSdeAccessor Sde => _sde ??= BuildStore(FullCatalog);

    private SqliteSdeAccessor BuildStore(Dictionary<string, string[]> datasets)
    {
        Directory.CreateDirectory(_dir);
        var zipPath = Path.Combine(_dir, $"sde-{Guid.NewGuid():N}.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach (var (name, lines) in datasets)
            {
                using var entry = new StreamWriter(zip.CreateEntry(name).Open());
                foreach (var line in lines)
                    entry.WriteLine(line);
            }

        var dbPath = Path.Combine(_dir, $"sde-{Guid.NewGuid():N}.db");
        new SdeSqliteBuilder().Build(zipPath, dbPath,
            new SdeVersion(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            progress: null, TestContext.Current.CancellationToken);
        return new SqliteSdeAccessor(dbPath);
    }

    private Dictionary<string, string[]> Without(string dataset)
    {
        var copy = new Dictionary<string, string[]>(FullCatalog);
        copy.Remove(dataset);
        return copy;
    }

    // AC-1: the agent table exists and carries the level.
    [Fact]
    public void FindAgentByName_ResolvesTheAgent_WithLevelAndAgentType()
    {
        var agent = Sde.FindAgentByName("Aralin Jick");

        Assert.NotNull(agent);
        Assert.Equal(4, agent!.Level);
        Assert.Equal(10, agent.AgentTypeId);
        Assert.Equal("EpicArcAgent", agent.AgentTypeName);
    }

    // Tegenproef AC-1: without npcCharacters.jsonl the lookup returns nothing.
    [Fact]
    public void FindAgentByName_WithoutNpcCharacters_ReturnsNull()
    {
        var sde = BuildStore(Without("npcCharacters.jsonl"));

        Assert.Null(sde.FindAgentByName("Aralin Jick"));
    }

    // AC-2: only npcCharacters rows with an `agent` sub-object become an agent.
    [Fact]
    public void Import_SkipsNpcCharactersWithoutAnAgentSubObject()
    {
        Assert.NotNull(Sde.FindAgentByName("Aralin Jick"));
        Assert.Null(Sde.FindAgentByName("Some Corp CEO"));
        Assert.Null(Sde.GetAgent(3000001));
    }

    // AC-3: agent name -> station -> solar system, sluitend.
    [Fact]
    public void FindAgentByName_ResolvesTheSolarSystem_ThroughItsStation()
    {
        var agent = Sde.FindAgentByName("Aralin Jick");

        Assert.Equal(30005040, agent!.SolarSystemId);
        Assert.Equal("Nishah", agent.SolarSystemName);
    }

    // Tegenproef AC-3: without npcStations.jsonl the system cannot be resolved and stays null.
    [Fact]
    public void FindAgentByName_WithoutNpcStations_LeavesTheSystemNull()
    {
        var sde = BuildStore(Without("npcStations.jsonl"));

        var agent = sde.FindAgentByName("Aralin Jick");

        Assert.NotNull(agent);
        Assert.Null(agent!.SolarSystemId);
        Assert.Null(agent.SolarSystemName);
    }

    // AC-4: searching on a non-English spelling resolves to the same agent (the TypeNameAlias route).
    [Fact]
    public void FindAgentByName_MatchesALocaleAlias_AndReturnsTheSameAgentAsTheEnglishName()
    {
        Assert.Equal(3019407, Sde.FindAgentByName("Aralin Jick")!.AgentId);
        Assert.Equal(3019407, Sde.FindAgentByName("Aralin Jick DE")!.AgentId);
    }

    // AC-5: mission and site id spaces are disjunct in storage even where the numbers coincide.
    [Fact]
    public void KillMissionDungeonId_AndSiteDungeonId_AreUnrelatedRows_ThatHappenToShareANumber()
    {
        var site = Assert.Single(Sde.SearchSites(), s => s.DungeonId == 13341);
        Assert.Equal("Unrelated Site At 13341", site.Name);

        var mission = Sde.GetMission(9001);
        Assert.Equal(13341, mission!.KillMissionDungeonId);
        Assert.Equal("Paragon Requests: Ships for Tips", mission.Name);
    }

    // AC-6: an epic-arc mission resolves its arc.
    [Fact]
    public void GetMission_ResolvesTheEpicArc()
    {
        var mission = Sde.GetMission(9001);

        Assert.Equal(77, mission!.ArcId);
    }

    // Tegenproef AC-6: without epicArcs.jsonl the same mission carries no arc.
    [Fact]
    public void GetMission_WithoutEpicArcs_CarriesNoArc()
    {
        var sde = BuildStore(Without("epicArcs.jsonl"));

        var mission = sde.GetMission(9001);

        Assert.NotNull(mission);
        Assert.Null(mission!.ArcId);
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
