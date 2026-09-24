using System;
using System.IO;
using System.IO.Compression;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Sde.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class SdeSkillCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sde-skills-{Guid.NewGuid():N}");
    private SqliteSdeAccessor? _sde;
    private SqliteSdeAccessor Sde => _sde ??= BuildStore();

    private SqliteSdeAccessor BuildStore()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "groups.jsonl"),
            """{"_key":9001,"categoryID":16,"name":{"en":"Fleet Support"},"published":true}""" + Environment.NewLine);
        File.WriteAllText(Path.Combine(_dir, "types.jsonl"),
            """{"_key":11572,"groupID":9001,"name":{"en":"Wing Command"},"description":{"en":"Improved proficiency at projecting fleet bonuses."},"published":true}""" + Environment.NewLine +
            """{"_key":90002,"groupID":9001,"name":{"en":"Hidden Skill"},"published":false}""" + Environment.NewLine);
        File.WriteAllText(Path.Combine(_dir, "typeDogma.jsonl"),
            """{"_key":11572,"dogmaAttributes":[{"attributeID":275,"value":8},{"attributeID":180,"value":164},{"attributeID":181,"value":168}]}""" + Environment.NewLine +
            """{"_key":90002,"dogmaAttributes":[{"attributeID":275,"value":1},{"attributeID":180,"value":164},{"attributeID":181,"value":168}]}""" + Environment.NewLine);

        var zipPath = Path.Combine(Path.GetTempPath(), $"sde-skills-{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(_dir, zipPath);
        var dbPath = Path.Combine(_dir, "sde.db");
        new SdeSqliteBuilder().Build(zipPath, dbPath,
            new SdeVersion(1, DateTimeOffset.UnixEpoch), null, TestContext.Current.CancellationToken);
        File.Delete(zipPath);
        return new SqliteSdeAccessor(dbPath);
    }

    [Fact]
    public void GetSkillsInGroup_ReturnsPublishedWingCommand_WithRankAndAttributes()
    {
        SdeSkill skill = Assert.Single(Sde.GetSkillsInGroup(9001));

        Assert.Equal(11572, skill.TypeId);
        Assert.Equal("Wing Command", skill.Name);
        Assert.Equal(8, skill.Rank);
        Assert.Equal(164, skill.PrimaryAttributeId);
        Assert.Equal(168, skill.SecondaryAttributeId);
        Assert.True(skill.Published);
    }

    [Fact]
    public void GetType_AfterFreshImport_ReturnsEnglishDescription()
    {
        SdeType skill = Assert.IsType<SdeType>(Sde.GetType(11572));

        Assert.StartsWith("Improved proficiency at projecting", skill.Description);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
