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
/// ET-146 deel A + D: a mutated (abyssal) type is recognisable from the SDE (metaGroupId 15 + category
/// Module/Drone), and dynamicItemAttributes.jsonl's per-attribute roll ranges import and read back verbatim.
/// End-to-end through the real <see cref="SdeSqliteBuilder"/> + <see cref="SqliteSdeAccessor"/> against an
/// in-test zip, no network.
/// </summary>
public sealed class SdeMutatedTypeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sde-mutated-{Guid.NewGuid():N}");

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

    // Deel A. Raymond's own "Simulated Stratios Fitting" (ET-146): 19 item lines / 17 distinct names, of which
    // exactly one — 50MN Abyssal Microwarpdrive (47408) — is a mutated module. Type ids for the other 16 are
    // synthetic (this test exercises the metaGroupId+category predicate, not real SDE content); 19023/31916 are
    // the real ids for the two also independently verified in the ticket's research. Type 900018 is not part of
    // the fixture — it is a mutaplasmid commodity (metaGroupId 15, category 17 Commodity) added to prove the
    // category filter excludes the mutaplasmids themselves, which is the trap a metaGroupId-only check would fail.
    [Fact]
    public void IsMutatedType_RecognisesOnlyTheAbyssalModule_InRaymondsFixture()
    {
        var sde = BuildStore(new Dictionary<string, string[]>
        {
            ["categories.jsonl"] =
            [
                """{"_key":7,"name":{"en":"Module"},"published":true}""",
                """{"_key":17,"name":{"en":"Commodity"},"published":true}"""
            ],
            ["groups.jsonl"] =
            [
                """{"_key":46,"categoryID":7,"name":{"en":"Propulsion Module"},"published":true}""",
                """{"_key":300,"categoryID":7,"name":{"en":"Generic Module"},"published":true}""",
                """{"_key":1964,"categoryID":17,"name":{"en":"Mutaplasmids"},"published":true}"""
            ],
            ["types.jsonl"] =
            [
                """{"_key":47408,"groupID":46,"name":{"en":"50MN Abyssal Microwarpdrive"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":15}""",
                """{"_key":19023,"groupID":300,"name":{"en":"Centum C-Type Medium Armor Repairer"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":4}""",
                """{"_key":31916,"groupID":300,"name":{"en":"Imperial Navy 800mm Steel Plates"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":4}""",
                """{"_key":900004,"groupID":300,"name":{"en":"Multispectrum Energized Membrane II"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":2}""",
                """{"_key":900005,"groupID":300,"name":{"en":"Drone Damage Amplifier II"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":2}""",
                """{"_key":900006,"groupID":300,"name":{"en":"Damage Control II"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":2}""",
                """{"_key":900007,"groupID":300,"name":{"en":"Ligature Integrated Analyzer"},"published":true,"mass":0,"volume":5,"capacity":0}""",
                """{"_key":900008,"groupID":300,"name":{"en":"Sentient Omnidirectional Tracking Link"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":5}""",
                """{"_key":900009,"groupID":300,"name":{"en":"Cargo Scanner II"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":2}""",
                """{"_key":900010,"groupID":300,"name":{"en":"Republic Fleet Large Cap Battery"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":4}""",
                """{"_key":900011,"groupID":300,"name":{"en":"Small Tractor Beam II"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":2}""",
                """{"_key":900012,"groupID":300,"name":{"en":"Focused Medium Pulse Laser II"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":2}""",
                """{"_key":900013,"groupID":300,"name":{"en":"Sisters Core Probe Launcher"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":4}""",
                """{"_key":900014,"groupID":300,"name":{"en":"Covert Ops Cloaking Device II"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":2}""",
                """{"_key":900015,"groupID":300,"name":{"en":"Medium Semiconductor Memory Cell II"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":2}""",
                """{"_key":900016,"groupID":300,"name":{"en":"Medium Nanobot Accelerator II"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":2}""",
                """{"_key":900017,"groupID":300,"name":{"en":"Medium Explosive Armor Reinforcer I"},"published":true,"mass":0,"volume":5,"capacity":0}""",
                """{"_key":900018,"groupID":1964,"name":{"en":"Some Unstable Mutaplasmid"},"published":true,"mass":0,"volume":5,"capacity":0,"metaGroupID":15}"""
            ]
        });

        Assert.True(sde.IsMutatedType(47408), "the 50MN Abyssal Microwarpdrive must be recognised as mutated");

        int[] notMutated =
        [
            19023, 31916, 900004, 900005, 900006, 900007, 900008, 900009, 900010,
            900011, 900012, 900013, 900014, 900015, 900016, 900017, 900018
        ];
        foreach (var typeId in notMutated)
            Assert.False(sde.IsMutatedType(typeId), $"type {typeId} must not be recognised as mutated");
    }

    // Deel D. dynamicItemAttributes.jsonl, one line per mutaplasmid: attributeIDs is a min/max multiplier per
    // dogma attribute. Shape verified in the ticket's research (413 lines, build 3494416); the mutaplasmid id and
    // its attribute ids here are the ticket's own example (47297 rolling 47408).
    [Fact]
    public void GetMutaplasmidAttributeRanges_RoundTripsTheFileValues_ForOneMutaplasmid()
    {
        var sde = BuildStore(new Dictionary<string, string[]>
        {
            ["dynamicItemAttributes.jsonl"] =
            [
                """{"_key":47297,"attributeIDs":[{"_key":6,"min":0.6,"max":1.4},{"_key":54,"min":0.75,"max":1.25}],"inputOutputMapping":[{"applicableTypes":[5975,12052],"resultingType":47408}]}"""
            ]
        });

        var ranges = sde.GetMutaplasmidAttributeRanges(47297);

        Assert.Equal(2, ranges.Count);
        Assert.Contains(ranges, r => r.AttributeId == 6 && r.Min == 0.6 && r.Max == 1.4);
        Assert.Contains(ranges, r => r.AttributeId == 54 && r.Min == 0.75 && r.Max == 1.25);
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
