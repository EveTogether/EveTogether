using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-231: the outcome (completed/failed/unknown, or AAR's own wave count) travels on the same bundled decision as
/// ET-230's attendance list, through the same <see cref="SetRunAttendanceCommand"/> — one FC decision at STOP, not
/// two. Every run this touches ends up carrying which <c>HomefrontPayoutTable</c> entry its own expected figure was
/// computed against, so two clients on two app versions can never silently disagree about the same site.
/// </summary>
public sealed class HomefrontOutcomeTests
{
    private const long Jithran = 90000020;
    private static readonly DateTime StartedAtUtc = new(2026, 9, 9, 20, 54, 0, DateTimeKind.Utc);

    // Dungeon id for "Raid: Hall of Sacrifice" (domain/homefronts.md §2) — resolves to the 5-person curve.
    private const int HallOfSacrifice = 10347;

    // Dungeon id for "Abyssal Artifact Recovery" — the one kind with a wave count instead of an outcome.
    private const int Aar = 10346;

    [AvaloniaFact]
    public async Task SetRunAttendanceCommand_WithCompletedOutcome_StampsOutcomeAndTableVersion()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Jithran, ActivityKind.Site, StartedAtUtc,
            HallOfSacrifice, "Raid: Hall of Sacrifice", 30000142), cancellationToken);
        RunAttendanceDecision decision = new(
            [new RunAttendanceEntryInput { CharacterId = Jithran, IsInSite = true, Reason = AttendanceReason.DamageDealt }],
            NotOnRosterCount: 0, AttendanceSource.Pilot, Jithran, StartedAtUtc.AddMinutes(20),
            Outcome: HomefrontOutcome.Completed);

        Result<int> written = await dispatcher.Send(new SetRunAttendanceCommand(decision, [Jithran], RunId: started.Value),
            cancellationToken);

        Assert.Equal(1, written.Value);
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == started.Value, cancellationToken);

        Assert.Equal(HomefrontOutcome.Completed, run.HomefrontOutcome);
        Assert.Equal(1, run.AttendanceCount);
        // 19 Mar 2026's table, the only one known — see HomefrontPayoutTableTests for the pre-that-date case.
        Assert.Equal("2026-03-19", run.HomefrontPayoutTableVersion);

        // The decision reconstructed from the run round-trips the outcome — RunSynchronizationApplier and every
        // read query reuse this same factory.
        RunAttendanceDecision? roundTripped = RunAttendanceDecision.Of(run);
        Assert.Equal(HomefrontOutcome.Completed, roundTripped?.Outcome);
    }

    [AvaloniaFact]
    public async Task SetRunAttendanceCommand_WithoutOutcome_StampsNoTableVersion()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Jithran, ActivityKind.Site, StartedAtUtc,
            HallOfSacrifice, "Raid: Hall of Sacrifice", 30000142), cancellationToken);
        RunAttendanceDecision decision = new(
            [new RunAttendanceEntryInput { CharacterId = Jithran, IsInSite = true, Reason = AttendanceReason.DamageDealt }],
            0, AttendanceSource.Pilot, Jithran, StartedAtUtc.AddMinutes(20));

        await dispatcher.Send(new SetRunAttendanceCommand(decision, [Jithran], RunId: started.Value), cancellationToken);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == started.Value, cancellationToken);

        Assert.Null(run.HomefrontOutcome);
        Assert.Null(run.HomefrontPayoutTableVersion);
    }

    [AvaloniaFact]
    public async Task SetRunAttendanceCommand_Aar_StoresCompletedWaveCountInsteadOfOutcome()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Jithran, ActivityKind.Site, StartedAtUtc,
            Aar, "Abyssal Artifact Recovery", 30000142), cancellationToken);
        RunAttendanceDecision decision = new(
            [new RunAttendanceEntryInput { CharacterId = Jithran, IsInSite = true, Reason = AttendanceReason.Mined }],
            0, AttendanceSource.Pilot, Jithran, StartedAtUtc.AddMinutes(20), CompletedWaveCount: 6);

        await dispatcher.Send(new SetRunAttendanceCommand(decision, [Jithran], RunId: started.Value), cancellationToken);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().SingleAsync(candidate => candidate.Id == started.Value, cancellationToken);

        Assert.Null(run.HomefrontOutcome);
        Assert.Equal(6, run.HomefrontCompletedWaveCount);
        Assert.NotNull(run.HomefrontPayoutTableVersion);
    }

    /// <summary>AC-2, end to end: six characters in the fleet, one hauler kept outside the site — N = 5. The five
    /// ticked characters each expect Raid's own N = 5 figure; the hauler, ticked out, expects nothing.</summary>
    [AvaloniaFact]
    public async Task ExpectedPayout_SixInFleetOneHauler_MatchesN5ForTheFiveAndNothingForTheHauler()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const long hauler = Jithran + 1;
        Result<Guid> pilotRun = await dispatcher.Send(new StartRunCommand(Jithran, ActivityKind.Site, StartedAtUtc,
            HallOfSacrifice, "Raid: Hall of Sacrifice", 30000142, GroupCode: "HF-N5"), cancellationToken);
        Result<Guid> haulerRun = await dispatcher.Send(new StartRunCommand(hauler, ActivityKind.Site, StartedAtUtc,
            HallOfSacrifice, "Raid: Hall of Sacrifice", 30000142, GroupCode: "HF-N5"), cancellationToken);

        RunAttendanceDecision decision = new(
            [
                new RunAttendanceEntryInput { CharacterId = Jithran, IsInSite = true, Reason = AttendanceReason.DamageDealt },
                new RunAttendanceEntryInput { CharacterId = hauler, IsInSite = false, Reason = AttendanceReason.NoActivityLogged }
            ],
            NotOnRosterCount: 4, // four more ticked pilots this test does not otherwise model — N = 1 (Jithran, ticked) + 4 = 5.
            AttendanceSource.FleetCommander, Jithran, StartedAtUtc.AddMinutes(20), Outcome: HomefrontOutcome.Completed);
        await dispatcher.Send(new SetRunAttendanceCommand(decision, [Jithran, hauler], "HF-N5"), cancellationToken);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run pilot = await db.Set<Run>().SingleAsync(candidate => candidate.Id == pilotRun.Value, cancellationToken);
        Run hauledRun = await db.Set<Run>().SingleAsync(candidate => candidate.Id == haulerRun.Value, cancellationToken);

        Assert.Equal(5, pilot.AttendanceCount);
        Assert.Equal(15_000_000m, RunIskFactsReader.HomefrontExpectedPayout(pilot));
        Assert.Equal(5, hauledRun.AttendanceCount);
        Assert.Null(RunIskFactsReader.HomefrontExpectedPayout(hauledRun));
    }
}
