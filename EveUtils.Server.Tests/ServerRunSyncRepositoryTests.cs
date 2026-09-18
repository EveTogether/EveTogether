using System.Text.Json;
using EveUtils.Server.Runs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EveUtils.Server.Tests;

public sealed class ServerRunSyncRepositoryTests
{
    private readonly SqliteServerDbContextFactory _factory = new();

    [Fact]
    public async Task Upsert_SameRunTwice_PersistsOneRow()
    {
        var repository = new ServerRunSyncRepository((IDbContextFactory<ServerDbContext>)_factory);
        var run = _Run("HF-7QK2");

        await repository.UpsertAsync(run, TestContext.Current.CancellationToken);
        run.SiteName = "Updated Homefront";
        await repository.UpsertAsync(run, TestContext.Current.CancellationToken);

        await using ServerDbContext db = ((IDbContextFactory<ServerDbContext>)_factory).CreateDbContext();
        Run stored = Assert.Single(await db.Set<Run>().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Updated Homefront", stored.SiteName);
    }

    [Fact]
    public async Task Upsert_OlderRevision_DoesNotOverwriteNewerRun()
    {
        var repository = new ServerRunSyncRepository((IDbContextFactory<ServerDbContext>)_factory);
        Run newer = _Run("HF-7QK2");
        newer.Revision = 3;
        await repository.UpsertAsync(newer, TestContext.Current.CancellationToken);

        Run older = _Run("HF-7QK2");
        older.Id = newer.Id;
        older.Revision = 2;
        older.SiteName = "Stale Homefront";
        await repository.UpsertAsync(older, TestContext.Current.CancellationToken);

        await using ServerDbContext db = ((IDbContextFactory<ServerDbContext>)_factory).CreateDbContext();
        Run stored = Assert.Single(await db.Set<Run>().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, stored.Revision);
        Assert.Equal("Homefront", stored.SiteName);
    }

    [Fact]
    public async Task PullChanged_Tombstone_ReturnsDeletedRun()
    {
        var repository = new ServerRunSyncRepository((IDbContextFactory<ServerDbContext>)_factory);
        var run = _Run("HF-7QK2");
        run.DeletedAtUtc = DateTime.UtcNow;
        Run member = _Run("HF-7QK2");
        member.CharacterId = 90000002;

        await repository.UpsertAsync(run, TestContext.Current.CancellationToken);
        await repository.UpsertAsync(member, TestContext.Current.CancellationToken);

        IReadOnlyList<Run> pulled = await repository.ListChangedAsync(member.CharacterId, ["HF-7QK2"], DateTime.UnixEpoch,
            TestContext.Current.CancellationToken);

        Run tombstone = Assert.Single(pulled, candidate => candidate.Id == run.Id);
        Assert.NotNull(tombstone.DeletedAtUtc);
    }

    [Fact]
    public async Task PullChanged_RequesterWithoutGroupRun_ReturnsNothing()
    {
        var repository = new ServerRunSyncRepository((IDbContextFactory<ServerDbContext>)_factory);
        Run run = _Run("HF-7QK2");
        await repository.UpsertAsync(run, TestContext.Current.CancellationToken);

        IReadOnlyList<Run> pulled = await repository.ListChangedAsync(90000002, ["HF-7QK2"], DateTime.UnixEpoch,
            TestContext.Current.CancellationToken);

        Assert.Empty(pulled);
    }

    /// <summary>ET-311: a server tab's read hands a character its own runs — a solo one included, which the group
    /// pull never could — and its group mates' under that pull's own holder rule; never a stranger's, a tombstone, or
    /// a run outside the window. Counter-proof: drop the <c>CharacterId == characterId</c> arm and the solo row goes
    /// red; drop the holder rule and the stranger's group run would come along.</summary>
    [Theory]
    [InlineData(90000001, null, false, 0, true)]
    [InlineData(90000002, "HF-7QK2", false, 0, true)]
    [InlineData(90000003, null, false, 0, false)]
    [InlineData(90000003, "HF-9XQ1", false, 0, false)]
    [InlineData(90000001, null, true, 0, false)]
    [InlineData(90000001, null, false, -40, false)]
    public async Task ListPublished_ReturnsOwnAndGroupMatesRuns_NeverStrangersOrDeleted(
        long characterId, string? groupCode, bool deleted, int startedDaysFromNow, bool listed)
    {
        var repository = new ServerRunSyncRepository((IDbContextFactory<ServerDbContext>)_factory);
        await repository.UpsertAsync(_Run("HF-7QK2"), TestContext.Current.CancellationToken);
        Run candidate = _Run(groupCode, characterId, deleted, startedDaysFromNow);
        await repository.UpsertAsync(candidate, TestContext.Current.CancellationToken);

        IReadOnlyList<Run> published = await repository.ListPublishedAsync(90000001,
            DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(1), TestContext.Current.CancellationToken);

        Assert.Equal(listed, published.Any(run => run.Id == candidate.Id));
    }

    [Fact]
    public void WirePayload_Run_DoesNotContainActivitySummary()
    {
        var payload = new RunWirePayload
        {
            Run = RunWireData.FromEntity(_Run("HF-7QK2")),
            SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        string json = JsonSerializer.Serialize(payload);

        Assert.DoesNotContain(nameof(ActivitySummary), json, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(ActivitySummary), typeof(RunWireData).GetProperties().Select(property => property.PropertyType));
    }

    private static Run _Run(string? groupCode, long characterId = 90000001, bool deleted = false, int startedDaysFromNow = 0) => new()
    {
        Id = Guid.CreateVersion7(),
        CharacterId = characterId,
        GroupCode = groupCode,
        ActivityKind = ActivityKind.Site,
        State = RunState.Saved,
        StartedAtUtc = DateTime.UtcNow.AddDays(startedDaysFromNow).AddMinutes(-10),
        StoppedAtUtc = DateTime.UtcNow.AddDays(startedDaysFromNow),
        DeletedAtUtc = deleted ? DateTime.UtcNow : null,
        SavedAtUtc = DateTime.UtcNow,
        SiteTypeId = 1234,
        SiteName = "Homefront",
        SyncState = RunSyncState.Pending,
        Revision = 2
    };
}
