using Avalonia.Headless.XUnit;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-219: measured during ET-217 that <c>RunBountyEntry</c> rows were only ever written by
/// <c>SaveRunCommandHandler</c>, from the <c>BountyEntries</c> list a caller happened to pass — never as the
/// payout itself arrived. <see cref="RunsOverviewViewModel"/>'s own save of an unfinished run
/// (<c>_SaveUnfinishedRunAsync</c>) sends four empty lists, so a run whose window disappeared before SAVE — a
/// crash, a closed app, or the 24h auto-save — lost every payout it had earned. Loot never had this problem
/// (<c>AddRunLootCaptureCommandHandler</c> already wrote each capture as it landed); these are the bounty
/// counterparts.
/// </summary>
public sealed class RunBountyLivePersistenceTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// AC-1/AC-3. Counter-proof, red against the code before this fix: <c>GamelogClientService.AddBountyAsync</c>
    /// only ever touched its own in-memory <c>_fleetRunBounty</c> dictionary and <c>CharacterMetricState</c> — no
    /// <c>RunBountyEntry</c> row existed until SAVE. Here nothing is ever saved; the row must already be there,
    /// which is what makes a crash mid-run survivable.
    /// </summary>
    [AvaloniaFact]
    public async Task BountyPayout_IsOnTheRunImmediately_BeforeAnySaveHappens()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        Assert.True(started.IsSuccess);

        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000001, "Pilot");
        await gamelog.AddBountyAsync("Pilot", new BountyEvent(StartedAtUtc.AddMinutes(1), 337_500));

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        RunBountyEntry entry = Assert.Single(await db.Set<RunBountyEntry>()
            .Where(e => e.RunId == started.Value).ToListAsync(cancellationToken));
        Assert.Equal(337_500m, entry.Isk);
    }

    /// <summary>
    /// AC-2. Reproduces <c>RunsOverviewViewModel._SaveUnfinishedRunAsync</c> exactly — the same four empty lists —
    /// against a run that earned bounty live and was then stopped without ever opening SAVE from the activity
    /// window. Counter-proof, red against the code before this fix: with bounty written only at SAVE, this exact
    /// call left <c>ActivitySummary.BountyIsk</c> at zero.
    /// </summary>
    [AvaloniaFact]
    public async Task UnfinishedRun_SavedFromTheOverview_KeepsTheBountyItAlreadyEarned()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        Guid runId = started.Value;

        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000001, "Pilot");
        await gamelog.AddBountyAsync("Pilot", new BountyEvent(StartedAtUtc.AddMinutes(1), 200_000));
        await gamelog.AddBountyAsync("Pilot", new BountyEvent(StartedAtUtc.AddMinutes(2), 86_875));

        DateTime stoppedAtUtc = StartedAtUtc.AddMinutes(10);
        await dispatcher.Send(new SetRunStoppedCommand(runId, stoppedAtUtc), cancellationToken);

        DateTime nowUtc = StartedAtUtc.AddMinutes(11);
        // The literal shape RunsOverviewViewModel._SaveUnfinishedRunAsync sends: nothing but the run id and times.
        Result saved = await dispatcher.Send(
            new SaveRunCommand(runId, stoppedAtUtc, nowUtc, [], [], [], []), cancellationToken);
        Assert.True(saved.IsSuccess);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        Assert.Equal(286_875m, summary.BountyIsk);
    }

    /// <summary>
    /// AC-4. Regression guard for the double-counting pitfall the ticket calls out: now that bounty is written the
    /// moment it arrives, SAVE must not write it a second time from a <c>BountyEntries</c> list of its own — the
    /// exact mistake reintroducing <c>ActivityWindowViewModel</c>'s old SAVE-time bounty synthesis alongside this
    /// live write would make (two rows, double the total).
    /// </summary>
    [AvaloniaFact]
    public async Task SavingAnOrdinaryRun_DoesNotDoubleCountBountyAlreadyWrittenLive()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        Guid runId = started.Value;

        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000001, "Pilot");
        await gamelog.AddBountyAsync("Pilot", new BountyEvent(StartedAtUtc.AddMinutes(1), 500_000));

        // The run's own SAVE (through the dispatcher directly, the same shape ActivityWindowViewModel.SaveRunAsync
        // now always sends) carries no bounty entries of its own — the live write already has it.
        Result saved = await dispatcher.Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        Assert.True(saved.IsSuccess);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        RunBountyEntry entry = Assert.Single(await db.Set<RunBountyEntry>()
            .Where(e => e.RunId == runId).ToListAsync(cancellationToken));
        Assert.Equal(500_000m, entry.Isk);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        Assert.Equal(500_000m, summary.BountyIsk);
    }

    /// <summary>
    /// AC-5. Two of the same pilot's characters, each with their own running run (ET-130/ET-210 shape) — the
    /// bounty each earns must land only on its own run, never the other's, purely from the live write (no group
    /// SAVE involved at all here).
    /// </summary>
    [AvaloniaFact]
    public async Task TwoCharactersEachRunning_EachLiveBountyLandsOnItsOwnRunOnly()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> firstRun = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        Result<Guid> secondRun = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000143), cancellationToken);

        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000001, "First Pilot");
        gamelog.MapCharacter(90000002, "Second Pilot");
        await gamelog.AddBountyAsync("First Pilot", new BountyEvent(StartedAtUtc.AddMinutes(1), 400_000));
        await gamelog.AddBountyAsync("Second Pilot", new BountyEvent(StartedAtUtc.AddMinutes(1), 900_000));

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        RunBountyEntry firstEntry = Assert.Single(await db.Set<RunBountyEntry>()
            .Where(e => e.RunId == firstRun.Value).ToListAsync(cancellationToken));
        RunBountyEntry secondEntry = Assert.Single(await db.Set<RunBountyEntry>()
            .Where(e => e.RunId == secondRun.Value).ToListAsync(cancellationToken));
        Assert.Equal(400_000m, firstEntry.Isk);
        Assert.Equal(900_000m, secondEntry.Isk);
    }
}
