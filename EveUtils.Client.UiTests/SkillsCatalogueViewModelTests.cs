using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using EveUtils.Client.ViewModels.Skills;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Sde.Storage;
using EveUtils.Shared.Modules.Skills.Entities;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-16 CATALOGUE feedback (Raymond, 2026-09-24): the time next to a not-yet-trained skill must say which level
/// it trains towards ("→ III · 0h 49m"), not a bare duration, and the level pips must mark a level actively
/// training with its own mark instead of showing it as already trained. Built directly against
/// <see cref="SkillsCatalogueViewModel"/> with a small real SDE store (same fixture shape as
/// SkillsQueueViewModelTests).
/// </summary>
public sealed class SkillsCatalogueViewModelTests : IDisposable
{
    private const int WingCommand = 11572;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sde-skillscatalogue-{Guid.NewGuid():N}");
    private SqliteSdeAccessor? _sde;
    private SqliteSdeAccessor Sde => _sde ??= _BuildStore();

    private static readonly CharacterAttributes Attributes = new()
    {
        CharacterId = 1, Charisma = 17, Intelligence = 17, Memory = 17, Perception = 27, Willpower = 21,
        TotalSp = 183_432_787, UnallocatedSp = 0
    };

    private SqliteSdeAccessor _BuildStore()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "groups.jsonl"),
            """{"_key":9001,"categoryID":16,"name":{"en":"Fleet Support"},"published":true}""" + Environment.NewLine);
        File.WriteAllText(Path.Combine(_dir, "types.jsonl"),
            """{"_key":11572,"groupID":9001,"name":{"en":"Wing Command"},"description":{"en":"Fleet support skill."},"published":true}""" + Environment.NewLine);
        File.WriteAllText(Path.Combine(_dir, "typeDogma.jsonl"),
            """{"_key":11572,"dogmaAttributes":[{"attributeID":275,"value":8},{"attributeID":180,"value":164},{"attributeID":181,"value":168}]}""" + Environment.NewLine);

        var zipPath = Path.Combine(Path.GetTempPath(), $"sde-skillscatalogue-{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(_dir, zipPath);
        var dbPath = Path.Combine(_dir, "sde.db");
        new SdeSqliteBuilder().Build(zipPath, dbPath, new SdeVersion(1, DateTimeOffset.UnixEpoch), null,
            TestContext.Current.CancellationToken);
        File.Delete(zipPath);
        return new SqliteSdeAccessor(dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
