using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-220: a pilot pressing DISCARD on their own, not-yet-saved run means "throw it away" — it should not linger as
/// an UNFINISHED run once the activity window is done with it. That is a different discard from the one
/// <see cref="HomefrontDiscardTests"/> covers: a fleet commander ending a shared run takes nothing from a member
/// (ET-105 AC-1), and <see cref="DiscardRunsInGroupCommand.DeleteAfterDiscard"/> is what keeps those two apart —
/// true only for the pilot's own direct discard, false for the fanout of someone else's.
/// </summary>
public sealed class RunDiscardDeleteTests
{
    private const string GroupCode = "HF-DSC1";
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The ticket's own counter-proof: a run thrown away with <c>DeleteAfterDiscard: true</c> is
    /// soft-deleted and no longer answers <see cref="GetUnfinishedRunsQuery"/>. Counter-proof: a
    /// <c>DiscardRunCommandHandler</c> that never soft-deletes (the shape before this fix) leaves it answering that
    /// query, since <c>RunDiscard.Apply</c> alone only ever moves a running run to <c>Stopped</c>.</summary>
    [AvaloniaFact]
    public async Task DiscardRunCommand_WithDeleteAfterDiscard_DoesNotAppearAsUnfinished()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site,
            StartedAtUtc, 1234, "Homefront", 30000142), cancellationToken);

        Result discarded = await dispatcher.Send(
            new DiscardRunCommand(started.Value, StartedAtUtc.AddMinutes(5), DeleteAfterDiscard: true),
            cancellationToken);
        Assert.True(discarded.IsSuccess);

        Result<IReadOnlyList<UnfinishedRunDto>> unfinished =
            await dispatcher.Query(new GetUnfinishedRunsQuery(), cancellationToken);
        Assert.Empty(unfinished.Value!);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == started.Value, cancellationToken);
        Assert.NotNull(run.DeletedAtUtc);
        Assert.Equal(RunState.Stopped, run.State);
    }

    /// <summary>ET-105 AC-1, still true with the new flag: a run already saved before the pilot discards it keeps
    /// its history, flag or no flag. Counter-proof: drop the <c>wasAlreadySaved</c> guard in the handler and this
    /// goes red with the saved run soft-deleted too.</summary>
    [AvaloniaFact]
    public async Task DiscardRunCommand_WithDeleteAfterDiscard_NeverDeletesAnAlreadySavedRun()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site,
            StartedAtUtc, 1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);

        await dispatcher.Send(
            new DiscardRunCommand(started.Value, StartedAtUtc.AddMinutes(20), DeleteAfterDiscard: true),
            cancellationToken);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == started.Value, cancellationToken);
        Assert.Null(run.DeletedAtUtc);
        Assert.Equal(RunState.Saved, run.State);
    }

    /// <summary>ET-210 side of the same ticket: throwing away a manual multi-toon group throws away every one of
    /// the pilot's own runs in it. Counter-proof: a handler that deletes only the first matching run goes red here
    /// with two of the three runs still answering <see cref="GetUnfinishedRunsQuery"/>.</summary>
    [AvaloniaFact]
    public async Task DiscardRunsInGroupCommand_WithDeleteAfterDiscard_DeletesEveryOwnRunInTheGroup()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid[] runIds =
        [
            await _StartAsync(dispatcher, 90000001, cancellationToken),
            await _StartAsync(dispatcher, 90000002, cancellationToken),
            await _StartAsync(dispatcher, 90000003, cancellationToken)
        ];

        Result<int> discarded = await dispatcher.Send(
            new DiscardRunsInGroupCommand(GroupCode, StartedAtUtc.AddMinutes(5), DeleteAfterDiscard: true),
            cancellationToken);
        Assert.True(discarded.IsSuccess);
        Assert.Equal(3, discarded.Value);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        foreach (Guid runId in runIds)
        {
            Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == runId, cancellationToken);
            Assert.NotNull(run.DeletedAtUtc);
        }

        Result<IReadOnlyList<UnfinishedRunDto>> unfinished =
            await dispatcher.Query(new GetUnfinishedRunsQuery(), cancellationToken);
        Assert.Empty(unfinished.Value!);
    }

    /// <summary>The valkuil the ticket names by name: a fleet commander ending a shared, not-yet-saved run must not
    /// take a member's own registration with it — only the commander's own explicit call sets
    /// <c>DeleteAfterDiscard</c>, the fanout <c>FleetRunGroupCodeCoordinator</c> sends never does. Counter-proof: a
    /// handler that deletes an unsaved run whenever it was not already saved, regardless of the flag (moving the
    /// delete into <c>RunDiscard.Apply</c> itself, exactly what the ticket warns against), goes red here with the
    /// member's run soft-deleted.</summary>
    [AvaloniaFact]
    public async Task DiscardRunsInGroupCommand_WithoutDeleteAfterDiscard_LeavesAnUnsavedMemberRunUndeleted()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid memberRun = await _StartAsync(dispatcher, 90000002, cancellationToken);

        // The fanout shape: no DeleteAfterDiscard, exactly what FleetRunGroupCodeCoordinator sends when the
        // commander ends the shared activity.
        Result<int> discarded = await dispatcher.Send(
            new DiscardRunsInGroupCommand(GroupCode, StartedAtUtc.AddMinutes(5)), cancellationToken);
        Assert.True(discarded.IsSuccess);
        Assert.Equal(1, discarded.Value);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == memberRun, cancellationToken);
        Assert.Null(run.DeletedAtUtc);
        Assert.Equal(RunState.Stopped, run.State);

        // Still there, and still theirs to decide about.
        Result<IReadOnlyList<UnfinishedRunDto>> unfinished =
            await dispatcher.Query(new GetUnfinishedRunsQuery(), cancellationToken);
        Assert.Single(unfinished.Value!);
    }

    private static async Task<Guid> _StartAsync(IDispatcher dispatcher, long characterId,
        CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site,
            StartedAtUtc, 1234, "Homefront", 30000142, GroupCode), cancellationToken);
        Assert.True(started.IsSuccess);
        return started.Value;
    }
}
