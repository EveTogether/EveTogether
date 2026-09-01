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
    public async Task PullChanged_Tombstone_ReturnsDeletedRun()
    {
        var repository = new ServerRunSyncRepository((IDbContextFactory<ServerDbContext>)_factory);
        var run = _Run("HF-7QK2");
        run.DeletedAtUtc = DateTime.UtcNow;

        await repository.UpsertAsync(run, TestContext.Current.CancellationToken);

        IReadOnlyList<Run> pulled = await repository.ListChangedAsync(["HF-7QK2"], DateTime.UnixEpoch, TestContext.Current.CancellationToken);

        Run tombstone = Assert.Single(pulled);
        Assert.NotNull(tombstone.DeletedAtUtc);
    }

    [Fact]
    public void WirePayload_Run_DoesNotContainActivitySummary()
    {
        var payload = new RunWirePayload { Run = _Run("HF-7QK2"), SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

        string json = JsonSerializer.Serialize(payload);

        Assert.DoesNotContain(nameof(ActivitySummary), json, StringComparison.Ordinal);
    }

    private static Run _Run(string groupCode) => new()
    {
        Id = Guid.CreateVersion7(),
        CharacterId = 90000001,
        GroupCode = groupCode,
        ActivityKind = ActivityKind.Site,
        State = RunState.Saved,
        StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
        StoppedAtUtc = DateTime.UtcNow,
        SavedAtUtc = DateTime.UtcNow,
        SiteTypeId = 1234,
        SiteName = "Homefront",
        SyncState = RunSyncState.Pending,
        Revision = 2
    };
}
