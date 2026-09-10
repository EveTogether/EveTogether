using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-214: an activity shown as one row in the overview is a group of runs since ET-210, so deleting "the activity"
/// must take every run in the group with it, not one out of it. These test the command layer directly — the runs
/// overview's own use of <see cref="DeleteRunsInGroupCommand"/> and <see cref="RestoreRunsInGroupCommand"/> is
/// covered from the screen side in <c>RunsOverviewTests</c>.
/// </summary>
public sealed class RunDeleteTests
{
    private const string GroupCode = "HF-DEL1";
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The ticket's own counter-proof: a group of three saved runs, deleted as one activity, leaves all
    /// three soft-deleted and a run outside the group untouched. Counter-proof: a handler that deletes only the
    /// first matching run (the single-run <see cref="DeleteRunCommand"/>'s own shape, mis-applied to a group) goes
    /// red here with two of the three runs still undeleted.</summary>
    [AvaloniaFact]
    public async Task DeleteRunsInGroupCommand_DeletesEveryRunInTheGroup_AndLeavesRunsOutsideItAlone()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid[] grouped =
        [
            await _SaveAsync(dispatcher, 90000001, GroupCode, cancellationToken),
            await _SaveAsync(dispatcher, 90000002, GroupCode, cancellationToken),
            await _SaveAsync(dispatcher, 90000003, GroupCode, cancellationToken)
        ];
        Guid outside = await _SaveAsync(dispatcher, 90000004, "HF-0TH3", cancellationToken);
        DateTime deletedAtUtc = StartedAtUtc.AddHours(1);

        Result<int> deleted = await dispatcher.Send(new DeleteRunsInGroupCommand(GroupCode, deletedAtUtc), cancellationToken);

        Assert.True(deleted.IsSuccess);
        Assert.Equal(3, deleted.Value);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        foreach (Guid runId in grouped)
        {
            Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == runId, cancellationToken);
            Assert.Equal(deletedAtUtc, run.DeletedAtUtc);
        }

        Run untouched = await db.Set<Run>().SingleAsync(candidate => candidate.Id == outside, cancellationToken);
        Assert.Null(untouched.DeletedAtUtc);
    }

    /// <summary>A running (or stopped-unfinished) sibling that happens to carry the same group code is not part of
    /// the saved activity this delete stands for — DISCARD is the way out for that one, not this. Counter-proof:
    /// drop the <c>State == Saved</c> filter from the handler and this goes red with the running run deleted too.</summary>
    [AvaloniaFact]
    public async Task DeleteRunsInGroupCommand_DoesNotTouchARunningSiblingInTheSameGroup()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await _SaveAsync(dispatcher, 90000001, GroupCode, cancellationToken);
        Result<Guid> running = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site,
            StartedAtUtc, 1234, "Homefront", 30000142, GroupCode), cancellationToken);
        Assert.True(running.IsSuccess);

        Result<int> deleted = await dispatcher.Send(
            new DeleteRunsInGroupCommand(GroupCode, StartedAtUtc.AddHours(1)), cancellationToken);

        Assert.True(deleted.IsSuccess);
        Assert.Equal(1, deleted.Value);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run stillRunning = await db.Set<Run>().SingleAsync(candidate => candidate.Id == running.Value, cancellationToken);
        Assert.Equal(RunState.Running, stillRunning.State);
        Assert.Null(stillRunning.DeletedAtUtc);
    }

    /// <summary>ET-214, review round 2: since ET-215 a group can hold a fleetmate's own run, pulled from a server
    /// and shown read-only because a correction to it could never be published back. The same reasoning applies to
    /// delete — soft-deleting it would flip its <c>SyncState</c> to <c>Pending</c>, offering to push a change to a
    /// run this machine does not own. <c>OnlyRunIds</c> is how the detail screen restricts a group delete to its
    /// own characters' runs. Counter-proof: drop the <c>OnlyRunIds</c> filter from the handler and this goes red
    /// with the "fleetmate" run deleted too.</summary>
    [AvaloniaFact]
    public async Task DeleteRunsInGroupCommand_WithOnlyRunIds_LeavesTheOtherGroupMembersRunsUntouched()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid ownRun = await _SaveAsync(dispatcher, 90000001, GroupCode, cancellationToken);
        Guid fleetmateRun = await _SaveAsync(dispatcher, 90000002, GroupCode, cancellationToken);
        DateTime deletedAtUtc = StartedAtUtc.AddHours(1);

        Result<int> deleted = await dispatcher.Send(
            new DeleteRunsInGroupCommand(GroupCode, deletedAtUtc, OnlyRunIds: [ownRun]), cancellationToken);

        Assert.True(deleted.IsSuccess);
        Assert.Equal(1, deleted.Value);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run deletedRun = await db.Set<Run>().SingleAsync(candidate => candidate.Id == ownRun, cancellationToken);
        Assert.Equal(deletedAtUtc, deletedRun.DeletedAtUtc);

        Run untouched = await db.Set<Run>().SingleAsync(candidate => candidate.Id == fleetmateRun, cancellationToken);
        Assert.Null(untouched.DeletedAtUtc);
        Assert.Equal(RunSyncState.Local, untouched.SyncState);
    }

    /// <summary>Soft delete's whole point: a group deleted by mistake goes straight back.</summary>
    [AvaloniaFact]
    public async Task RestoreRunsInGroupCommand_UndoesTheDelete()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid[] grouped =
        [
            await _SaveAsync(dispatcher, 90000001, GroupCode, cancellationToken),
            await _SaveAsync(dispatcher, 90000002, GroupCode, cancellationToken)
        ];
        await dispatcher.Send(new DeleteRunsInGroupCommand(GroupCode, StartedAtUtc.AddHours(1)), cancellationToken);

        Result<int> restored = await dispatcher.Send(new RestoreRunsInGroupCommand(GroupCode), cancellationToken);

        Assert.True(restored.IsSuccess);
        Assert.Equal(2, restored.Value);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        foreach (Guid runId in grouped)
        {
            Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == runId, cancellationToken);
            Assert.Null(run.DeletedAtUtc);
        }
    }

    /// <summary>The lone-run half of the same either/or the overview uses: an activity that was never grouped
    /// (<c>GroupCode</c> null) deletes and restores through the single-run commands instead.</summary>
    [AvaloniaFact]
    public async Task RestoreRunCommand_UndoesASingleRunDelete()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid runId = await _SaveAsync(dispatcher, 90000001, groupCode: null, cancellationToken);
        await dispatcher.Send(new DeleteRunCommand(runId, StartedAtUtc.AddHours(1)), cancellationToken);

        Result restored = await dispatcher.Send(new RestoreRunCommand(runId), cancellationToken);

        Assert.True(restored.IsSuccess);
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == runId, cancellationToken);
        Assert.Null(run.DeletedAtUtc);
    }

    private static async Task<Guid> _SaveAsync(IDispatcher dispatcher, long characterId, string? groupCode,
        CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site,
            StartedAtUtc, 1234, "Homefront", 30000142, groupCode), cancellationToken);
        Assert.True(started.IsSuccess);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        return started.Value;
    }
}
