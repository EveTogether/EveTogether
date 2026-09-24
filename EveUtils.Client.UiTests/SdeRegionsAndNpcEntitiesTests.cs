using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Sde.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The SDE import resolves a system's region and the SDE names for NPC corporations and factions (ET-335), so the
/// killmail importer can tell an NPC id from a player id without asking ESI. End-to-end through the real
/// <see cref="SdeSqliteBuilder"/> + <see cref="SqliteSdeAccessor"/> against an in-test zip, no network. Shapes
/// copied from build 3539543.
/// </summary>
public sealed class SdeRegionsAndNpcEntitiesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sde-regions-{Guid.NewGuid():N}");

    private static readonly Dictionary<string, string[]> Catalog = new()
    {
        ["mapRegions.jsonl"] = ["""{"_key":10000001,"name":{"en":"Derelik"}}"""],
        ["mapSolarSystems.jsonl"] =
        [
            """{"_key":30000001,"name":{"en":"Tanoo"},"securityStatus":0.858,"regionID":10000001}"""
        ],
        ["npcCorporations.jsonl"] = ["""{"_key":1000001,"name":{"en":"Doomheim"}}"""],
        ["factions.jsonl"] = ["""{"_key":500001,"name":{"en":"Caldari State"}}"""]
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
                {
                    entry.WriteLine(line);
                }
            }

        var dbPath = Path.Combine(_dir, "sde.db");
        new SdeSqliteBuilder().Build(zipPath, dbPath,
            new SdeVersion(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            progress: null, TestContext.Current.CancellationToken);
        return new SqliteSdeAccessor(dbPath);
    }

    // AC-1: system -> region resolves through regionId, no ESI involved.
    [Fact]
    public void GetSolarSystem_ResolvesTheRegion_ThroughRegionId()
    {
        var system = Assert.IsType<SdeSolarSystem>(Sde.GetSolarSystem(30000001));

        Assert.Equal("Tanoo", system.Name);
        Assert.Equal(0.858, system.SecurityStatus);
        Assert.Equal("Derelik", system.RegionName);
    }

    // AC-2: an NPC id resolves to its SDE name, and an id the SDE has never heard of (a player's) comes back null,
    // not an exception and not an empty string — the importer's signal to ask ESI instead.
    [Fact]
    public void GetNpcCorporationName_AndGetFactionName_ReturnTheSdeName_AndNullForAnUnknownId()
    {
        Assert.Equal("Doomheim", Sde.GetNpcCorporationName(1000001));
        Assert.Null(Sde.GetNpcCorporationName(98000001));

        Assert.Equal("Caldari State", Sde.GetFactionName(500001));
        Assert.Null(Sde.GetFactionName(999999));
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup of the throwaway store
        }
    }
}
