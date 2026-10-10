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
/// ET-501: blueprints.jsonl's manufacturing activity imports and reads back — product, materials, job time and the
/// production limit — and a blueprint that builds nothing is no blueprint here. Through the real
/// <see cref="SdeSqliteBuilder"/> + <see cref="SqliteSdeAccessor"/> against an in-test zip, no network.
/// </summary>
public sealed class SdeBlueprintManufacturingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sde-blueprints-{Guid.NewGuid():N}");

    // The line build 3586130 carries for 85957, verbatim, and a reaction formula: no manufacturing activity at all.
    private static readonly string[] Blueprints =
    [
        """{"_key": 85957, "activities": {"manufacturing": {"materials": [{"quantity": 1, "typeID": 47742}, {"quantity": 60, "typeID": 85391}, {"quantity": 60, "typeID": 85398}], "products": [{"quantity": 1, "typeID": 85474}], "skills": [{"level": 1, "typeID": 3380}], "time": 3600}}, "blueprintTypeID": 85957, "maxProductionLimit": 10}""",
        """{"_key": 46166, "activities": {"reaction": {"materials": [{"quantity": 100, "typeID": 16634}], "products": [{"quantity": 200, "typeID": 16663}], "time": 10800}}, "blueprintTypeID": 46166, "maxProductionLimit": 1000}"""
    ];

    [Fact]
    public void GetBlueprintManufacturing_ReadsBackWhatOneRunMakesAndTakes()
    {
        SqliteSdeAccessor sde = _BuildStore(new Dictionary<string, string[]> { ["blueprints.jsonl"] = Blueprints });

        SdeBlueprintManufacturing? blueprint = sde.GetBlueprintManufacturing(85957);

        Assert.NotNull(blueprint);
        Assert.Equal((85474, 1, 3600, 10), (blueprint.ProductTypeId, blueprint.ProductQuantity, blueprint.TimeSeconds,
            blueprint.MaxProductionLimit));
        Assert.Equivalent(
            new[] { new SdeBlueprintMaterial(47742, 1), new SdeBlueprintMaterial(85391, 60), new SdeBlueprintMaterial(85398, 60) },
            blueprint.Materials);
    }

    [Fact]
    public void GetBlueprintManufacturing_IsNull_ForAReactionFormulaAndAnUnknownType()
    {
        SqliteSdeAccessor sde = _BuildStore(new Dictionary<string, string[]> { ["blueprints.jsonl"] = Blueprints });

        Assert.Null(sde.GetBlueprintManufacturing(46166));
        Assert.Null(sde.GetBlueprintManufacturing(34));
    }

    private SqliteSdeAccessor _BuildStore(Dictionary<string, string[]> datasets)
    {
        Directory.CreateDirectory(_dir);
        string zipPath = Path.Combine(_dir, $"sde-{Guid.NewGuid():N}.zip");
        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach ((string name, string[] lines) in datasets)
            {
                using var entry = new StreamWriter(zip.CreateEntry(name).Open());
                foreach (string line in lines)
                    entry.WriteLine(line);
            }

        string dbPath = Path.Combine(_dir, $"sde-{Guid.NewGuid():N}.db");
        new SdeSqliteBuilder().Build(zipPath, dbPath,
            new SdeVersion(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            progress: null, TestContext.Current.CancellationToken);
        return new SqliteSdeAccessor(dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
