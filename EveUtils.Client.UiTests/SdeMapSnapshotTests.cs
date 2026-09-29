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
/// The SDE import carries the universe map (ET-391): constellations, 2D positions, stargate connections and
/// region/constellation factions, read back in one <see cref="SqliteSdeAccessor.GetMapSnapshot"/>. End-to-end
/// through the real <see cref="SdeSqliteBuilder"/> against an in-test zip; shapes copied from build 3542233.
/// </summary>
public sealed class SdeMapSnapshotTests : IDisposable
{
    private const int Jita = 30000142;
    private const int Perimeter = 30000144;
    private const int Urlen = 30000139;
    private const int Isolated = 30000999;
    private const int WormholeSystem = 31000005;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sde-map-{Guid.NewGuid():N}");

    // Jita<->Perimeter gates exist on both sides (the normal case); Urlen->Jita has no reverse gate in the file.
    private static readonly Dictionary<string, string[]> Catalog = new()
    {
        ["mapRegions.jsonl"] =
        [
            """{"_key":10000002,"name":{"en":"The Forge"},"factionID":500001,"constellationIDs":[20000020]}""",
            """{"_key":11000001,"name":{"en":"A-R00001"},"constellationIDs":[21000001]}"""
        ],
        ["mapConstellations.jsonl"] =
        [
            """{"_key":20000020,"name":{"en":"Kimotoro"},"regionID":10000002,"factionID":500001}""",
            """{"_key":21000001,"name":{"en":"A-C00001"},"regionID":11000001}"""
        ],
        ["mapSolarSystems.jsonl"] =
        [
            """{"_key":30000142,"name":{"en":"Jita"},"securityStatus":0.945913,"regionID":10000002,"constellationID":20000020,"position2D":{"x":1.5e17,"y":1.79e17}}""",
            """{"_key":30000144,"name":{"en":"Perimeter"},"securityStatus":0.9,"regionID":10000002,"constellationID":20000020,"position2D":{"x":1.6e17,"y":-2.0e16}}""",
            """{"_key":30000139,"name":{"en":"Urlen"},"securityStatus":0.5,"regionID":10000002,"constellationID":20000020,"position2D":{"x":1.4e17,"y":0}}""",
            """{"_key":30000999,"name":{"en":"Nowhere"},"securityStatus":0.1,"regionID":10000002,"constellationID":20000020,"position2D":{"x":0,"y":0}}""",
            """{"_key":31000005,"name":{"en":"J000102"},"securityStatus":-0.99,"regionID":11000001,"constellationID":21000001}"""
        ],
        ["mapStargates.jsonl"] =
        [
            """{"_key":50001248,"solarSystemID":30000142,"destination":{"solarSystemID":30000144,"stargateID":50001249}}""",
            """{"_key":50001249,"solarSystemID":30000144,"destination":{"solarSystemID":30000142,"stargateID":50001248}}""",
            """{"_key":50001300,"solarSystemID":30000139,"destination":{"solarSystemID":30000142,"stargateID":50001301}}"""
        ]
    };

    private SqliteSdeAccessor? _sde;
    private SqliteSdeAccessor Sde => _sde ??= BuildStore();

    private SqliteSdeAccessor BuildStore()
    {
        Directory.CreateDirectory(_dir);
        var zipPath = Path.Combine(_dir, "sde.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach (var (name, lines) in Catalog)
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

    [Fact]
    public void GetMapSnapshot_CountsMatchTheSource_ForSystemsWithPositionConstellationsRegionsAndJumps()
    {
        var snapshot = Sde.GetMapSnapshot();

        Assert.Equal(5, snapshot.Systems.Count);
        Assert.Equal(4, snapshot.Systems.Count(system => system.X2d is not null && system.Y2d is not null));
        Assert.Equal(2, snapshot.Constellations.Count);
        Assert.Equal(2, snapshot.Regions.Count);
        // 3 gates, 2 connections: the two Jita/Perimeter gates collapse into one row.
        Assert.Equal(2, snapshot.Jumps.Count);
    }

    [Fact]
    public void GetMapSnapshot_Jita_HasConstellationAndPosition_AndAWormholeSystemHasNullPosition()
    {
        var snapshot = Sde.GetMapSnapshot();

        var jita = Assert.Single(snapshot.Systems, system => system.SolarSystemId == Jita);
        Assert.Equal(20000020, jita.ConstellationId);
        Assert.Equal(10000002, jita.RegionId);
        Assert.Equal(1.5e17, jita.X2d);

        var wormhole = Assert.Single(snapshot.Systems, system => system.SolarSystemId == WormholeSystem);
        Assert.Null(wormhole.X2d);
        Assert.Null(wormhole.Y2d);
        Assert.Equal(21000001, wormhole.ConstellationId);
    }

    [Fact]
    public void GetMapSnapshot_NegatesY_SoNorthIsUp()
    {
        var snapshot = Sde.GetMapSnapshot();

        var jita = Assert.Single(snapshot.Systems, system => system.SolarSystemId == Jita);
        var perimeter = Assert.Single(snapshot.Systems, system => system.SolarSystemId == Perimeter);
        Assert.Equal(-1.79e17, jita.Y2d);
        Assert.Equal(2.0e16, perimeter.Y2d);
        Assert.True(jita.Y2d < perimeter.Y2d, "Jita lies north of Perimeter in the SDE, so it must have the smaller screen y");
    }

    [Fact]
    public void GetMapSnapshot_StoresOneJumpPerConnection_LowerIdFirst_EvenWhenOnlyOneGateIsListed()
    {
        var jumps = Sde.GetMapSnapshot().Jumps;

        Assert.Equal([new SdeMapJump(Urlen, Jita), new SdeMapJump(Jita, Perimeter)], jumps);
        Assert.All(jumps, jump => Assert.True(jump.FromSystemId < jump.ToSystemId));
        Assert.DoesNotContain(jumps, jump => jump.FromSystemId == Isolated || jump.ToSystemId == Isolated);
    }

    [Fact]
    public void GetMapSnapshot_CarriesFactionOnRegionAndConstellation_NullWhenTheSourceHasNone()
    {
        var snapshot = Sde.GetMapSnapshot();

        Assert.Equal(500001, Assert.Single(snapshot.Regions, region => region.RegionId == 10000002).FactionId);
        Assert.Null(Assert.Single(snapshot.Regions, region => region.RegionId == 11000001).FactionId);
        Assert.Equal(500001, Assert.Single(snapshot.Constellations, c => c.ConstellationId == 20000020).FactionId);
        Assert.Null(Assert.Single(snapshot.Constellations, c => c.ConstellationId == 21000001).FactionId);
        Assert.Equal("Kimotoro", snapshot.Constellations.Single(c => c.ConstellationId == 20000020).Name);
    }

    [Fact]
    public void GetMapSnapshot_WhenTheStoreIsUnavailable_ReturnsTheEmptySnapshot()
    {
        var sde = new SqliteSdeAccessor(Path.Combine(_dir, "missing.db"));

        var snapshot = sde.GetMapSnapshot();

        Assert.Same(SdeMapSnapshot.Empty, snapshot);
        Assert.Empty(snapshot.Systems);
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
