using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using EveUtils.Client.ViewModels.Skills;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Sde.Storage;
using EveUtils.Shared.Modules.Skills.Entities;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-16 AC3-5: TRAINING QUEUE never shows a row whose FinishDate has already passed, the queue-left duration
/// matches ESI's own dates exactly (RaymondKrah fixture: 252d 13h), and a paused queue (ESI omits every date)
/// shows ≈ estimates rather than dates. One test per acceptance criterion, built directly against
/// <see cref="SkillsQueueViewModel"/> with a small real SDE store (same fixture shape as SdeSkillCatalogTests).
/// </summary>
public sealed class SkillsQueueViewModelTests : IDisposable
{
    private const int WingCommand = 11572;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sde-skillsqueue-{Guid.NewGuid():N}");
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

        var zipPath = Path.Combine(Path.GetTempPath(), $"sde-skillsqueue-{Guid.NewGuid():N}.zip");
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

    /// <summary>Criterion 3. Red if an already-passed row is still shown.</summary>
    [Fact]
    public void Rows_NeverShowARowWhoseFinishDateHasAlreadyPassed()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new List<CharacterSkillQueueEntry>
        {
            new() { CharacterId = 1, QueuePosition = 0, SkillTypeId = WingCommand, FinishedLevel = 3,
                StartDate = now.AddDays(-10), FinishDate = now.AddDays(-1) },   // already finished — must not show
            new() { CharacterId = 1, QueuePosition = 1, SkillTypeId = WingCommand, FinishedLevel = 4,
                StartDate = now, FinishDate = now.AddDays(5) },
        };
        var snapshot = new SkillsCharacterSnapshot(Sde, new Dictionary<int, int>(), queue, Attributes, now);

        var vm = new SkillsQueueViewModel(snapshot);

        var row = Assert.Single(vm.Rows);
        Assert.Contains("IV", row.SkillText);
    }

    /// <summary>Criterion 4. Red if the queue-left duration drifts from what ESI's own FinishDate says.</summary>
    [Fact]
    public void QueueLeftText_MatchesEsisOwnFinishDate_252d13h()
    {
        var now = DateTimeOffset.UtcNow;
        var finish = now + TimeSpan.FromMinutes(252 * 1440 + 13 * 60); // 252d 13h, exact to the minute Until() rounds to
        var queue = new List<CharacterSkillQueueEntry>
        {
            new() { CharacterId = 1, QueuePosition = 0, SkillTypeId = WingCommand, FinishedLevel = 5,
                StartDate = now, FinishDate = finish },
        };
        var snapshot = new SkillsCharacterSnapshot(Sde, new Dictionary<int, int>(), queue, Attributes, now);

        var vm = new SkillsQueueViewModel(snapshot);

        Assert.Contains("252d 13h", vm.QueueLeftText);
    }

    /// <summary>Criterion 5. Red if a paused queue shows a date instead of a ≈ estimate.</summary>
    [Fact]
    public void PausedQueue_ShowsApproximateEstimates_NeverADate()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new List<CharacterSkillQueueEntry>
        {
            new() { CharacterId = 1, QueuePosition = 0, SkillTypeId = WingCommand, FinishedLevel = 3,
                StartDate = null, FinishDate = null },
            new() { CharacterId = 1, QueuePosition = 1, SkillTypeId = WingCommand, FinishedLevel = 4,
                StartDate = null, FinishDate = null },
        };
        var snapshot = new SkillsCharacterSnapshot(Sde, new Dictionary<int, int>(), queue, Attributes, now);

        var vm = new SkillsQueueViewModel(snapshot);

        Assert.True(vm.IsPaused);
        Assert.All(vm.Rows, row =>
        {
            Assert.Equal("—", row.EndsText);
            Assert.StartsWith("≈", row.ThisLevelText);
            Assert.StartsWith("≈", row.FromNowText);
        });
    }
}
