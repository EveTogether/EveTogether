using System;
using System.IO;
using System.IO.Compression;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Sde.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class NpcEnemySearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sde-npc-search-{Guid.NewGuid():N}");

    [Fact]
    public void SearchNpcEnemies_EscapesLikeWildcards()
    {
        var sde = BuildStore();

        Assert.Empty(sde.SearchNpcEnemies("%"));
        Assert.Equal(["Enemy_Name"], sde.SearchNpcEnemies("Enemy_Name").Select(enemy => enemy.Name));
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

    private SqliteSdeAccessor BuildStore()
    {
        Directory.CreateDirectory(_dir);
        var zipPath = Path.Combine(_dir, "sde.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            Write(zip, "groups.jsonl", """{"_key":1,"categoryID":11,"name":{"en":"Pirate Frigate"},"published":true}""");
            Write(zip, "types.jsonl",
                """{"_key":1,"groupID":1,"name":{"en":"Ordinary NPC"},"published":true,"mass":1,"volume":1,"capacity":1}""",
                """{"_key":2,"groupID":1,"name":{"en":"Enemy_Name"},"published":true,"mass":1,"volume":1,"capacity":1}""");
            Write(zip, "typeDogma.jsonl",
                """{"_key":1,"dogmaAttributes":[{"attributeID":114,"value":1}]}""",
                """{"_key":2,"dogmaAttributes":[{"attributeID":114,"value":1}]}""");
        }

        var dbPath = Path.Combine(_dir, "sde.db");
        new SdeSqliteBuilder().Build(zipPath, dbPath,
            new SdeVersion(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            progress: null, TestContext.Current.CancellationToken);
        return new SqliteSdeAccessor(dbPath);
    }

    private static void Write(ZipArchive zip, string name, params string[] lines)
    {
        using var entry = new StreamWriter(zip.CreateEntry(name).Open());
        foreach (string line in lines)
            entry.WriteLine(line);
    }
}
