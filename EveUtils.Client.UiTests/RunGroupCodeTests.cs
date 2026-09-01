using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Grouping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class RunGroupCodeTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_UsesReadableGroupCodeFormat()
    {
        string code = RunGroupCode.Create();

        Assert.Matches("^HF-[A-Z0-9]{4}$", code);
    }

    [Fact]
    public void AbyssalStarts_SameSecond_ConvergeOnOneCode()
    {
        RunGroupCodeCandidate firstClient = new("HF-7QK2", StartedAtUtc.AddMilliseconds(100));
        RunGroupCodeCandidate secondClient = new("HF-1A2B", StartedAtUtc.AddMilliseconds(900));

        Result<string> result = RunGroupCodeArbiter.Select(ActivityKind.Abyssal, [firstClient, secondClient]);

        Assert.True(result.IsSuccess);
        Assert.Equal("HF-7QK2", result.Value);
    }

    [Fact]
    public void AbyssalStarts_SameMillisecond_ConvergeDeterministically()
    {
        RunGroupCodeCandidate firstClient = new("HF-7QK2", StartedAtUtc);
        RunGroupCodeCandidate secondClient = new("HF-1A2B", StartedAtUtc);

        Result<string> result = RunGroupCodeArbiter.Select(ActivityKind.Abyssal, [firstClient, secondClient]);

        Assert.True(result.IsSuccess);
        Assert.Equal("HF-1A2B", result.Value);
    }

    [Fact]
    public void HomefrontStarts_UsesCommanderCode()
    {
        RunGroupCodeCandidate commander = new("HF-7QK2", StartedAtUtc, true);
        RunGroupCodeCandidate member = new("HF-1A2B", StartedAtUtc.AddMilliseconds(-1));

        Result<string> result = RunGroupCodeArbiter.Select(ActivityKind.Site, [member, commander]);

        Assert.True(result.IsSuccess);
        Assert.Equal("HF-7QK2", result.Value);
    }

    [Fact]
    public void HomefrontStarts_WithoutCommander_UsesEarliestCode()
    {
        RunGroupCodeCandidate first = new("HF-7QK2", StartedAtUtc);
        RunGroupCodeCandidate second = new("HF-1A2B", StartedAtUtc.AddMilliseconds(1));

        Result<string> result = RunGroupCodeArbiter.Select(ActivityKind.Site, [second, first]);

        Assert.True(result.IsSuccess);
        Assert.Equal("HF-7QK2", result.Value);
    }

    [Fact]
    public void Select_WithoutCandidates_ReturnsFailure()
    {
        Result<string> result = RunGroupCodeArbiter.Select(ActivityKind.Abyssal, []);

        Assert.False(result.IsSuccess);
    }

    [AvaloniaFact]
    public async Task AbyssalStarts_SameSecond_OnTwoClients_ConvergeOnOneCode()
    {
        var firstWire = new FleetWireTransport();
        var secondWire = new FleetWireTransport();
        using var firstClient = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(firstWire));
        using var secondClient = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(secondWire));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IDispatcher firstDispatcher = firstClient.Services.GetRequiredService<IDispatcher>();
        IDispatcher secondDispatcher = secondClient.Services.GetRequiredService<IDispatcher>();
        firstWire.Destination = secondClient.Services.GetRequiredService<IEventBus>();
        secondWire.Destination = firstClient.Services.GetRequiredService<IEventBus>();
        _ = firstClient.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();
        _ = secondClient.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();

        Task<Result<Guid>> firstStarted = firstDispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal,
            StartedAtUtc.AddMilliseconds(100), 0, null, null, FleetId: 42), cancellationToken);
        Task<Result<Guid>> secondStarted = secondDispatcher.Send(new StartRunCommand(90000002, ActivityKind.Abyssal,
            StartedAtUtc.AddMilliseconds(900), 0, null, null, FleetId: 42), cancellationToken);
        Result<Guid>[] started = await Task.WhenAll(firstStarted, secondStarted);

        Assert.All(started, result => Assert.True(result.IsSuccess));
        await using ClientDbContext firstDb = await firstClient.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        await using ClientDbContext secondDb = await secondClient.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        string? firstCode = Assert.Single(await firstDb.Set<Run>().ToListAsync(cancellationToken)).GroupCode;
        string? secondCode = Assert.Single(await secondDb.Set<Run>().ToListAsync(cancellationToken)).GroupCode;
        Assert.NotNull(firstCode);
        Assert.Equal(firstCode, secondCode);
    }

    [AvaloniaFact]
    public async Task LinkRunToGroupCode_LateRun_PreservesOwnStartTime()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTime lateArrivalUtc = StartedAtUtc.AddMinutes(4);

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Abyssal, lateArrivalUtc,
            0, null, null), cancellationToken);
        Result linked = await dispatcher.Send(new LinkRunToGroupCodeCommand(started.Value, "HF-7QK2"), cancellationToken);

        Assert.True(started.IsSuccess);
        Assert.True(linked.IsSuccess);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Run run = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.Equal("HF-7QK2", run.GroupCode);
        Assert.Equal(lateArrivalUtc, run.StartedAtUtc);
    }

    [AvaloniaFact]
    public async Task AbyssalLateArrival_TakesRunningCodeAndPreservesOwnStartTime()
    {
        var firstWire = new FleetWireTransport();
        var secondWire = new FleetWireTransport();
        using var firstClient = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(firstWire));
        using var secondClient = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(secondWire));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        firstWire.Destination = secondClient.Services.GetRequiredService<IEventBus>();
        secondWire.Destination = firstClient.Services.GetRequiredService<IEventBus>();
        _ = firstClient.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();
        _ = secondClient.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();
        DateTime lateArrivalUtc = StartedAtUtc.AddMinutes(4);

        Result<Guid> firstStarted = await firstClient.Services.GetRequiredService<IDispatcher>().Send(
            new StartRunCommand(90000001, ActivityKind.Abyssal, StartedAtUtc, 0, null, null, FleetId: 42), cancellationToken);
        Result<Guid> secondStarted = await secondClient.Services.GetRequiredService<IDispatcher>().Send(
            new StartRunCommand(90000002, ActivityKind.Abyssal, lateArrivalUtc, 0, null, null, FleetId: 42), cancellationToken);

        Assert.True(firstStarted.IsSuccess);
        Assert.True(secondStarted.IsSuccess);
        await using ClientDbContext firstDb = await firstClient.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        await using ClientDbContext secondDb = await secondClient.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Run firstRun = Assert.Single(await firstDb.Set<Run>().ToListAsync(cancellationToken));
        Run secondRun = Assert.Single(await secondDb.Set<Run>().ToListAsync(cancellationToken));
        Assert.Equal(firstRun.GroupCode, secondRun.GroupCode);
        Assert.Equal(lateArrivalUtc, secondRun.StartedAtUtc);
    }

    [AvaloniaFact]
    public async Task UnlinkRunFromGroupCode_CorrectedParticipant_PreservesRun()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000003, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);

        Result unlinked = await dispatcher.Send(new UnlinkRunFromGroupCodeCommand(started.Value), cancellationToken);

        Assert.True(unlinked.IsSuccess);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Run run = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.Null(run.GroupCode);
        Assert.Equal(StartedAtUtc, run.StartedAtUtc);
    }

    [AvaloniaFact]
    public async Task UnlinkRunFromGroupCode_CorrectedParticipant_ChangesBountyPerParticipant()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid participantRunId = await _SaveRunAsync(dispatcher, 90000001, 100m, cancellationToken);
        Guid stationRunId = await _SaveRunAsync(dispatcher, 90000002, 0m, cancellationToken);

        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        ActivitySummary beforeCorrection = Assert.Single(await db.Set<ActivitySummary>().AsNoTracking().ToListAsync(cancellationToken));
        decimal beforePerParticipant = beforeCorrection.BountyIsk / beforeCorrection.ParticipantCount;

        Result unlinked = await dispatcher.Send(new UnlinkRunFromGroupCodeCommand(stationRunId), cancellationToken);
        Result<int> rebuilt = await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        ActivitySummary afterCorrection = Assert.Single(await db.Set<ActivitySummary>().AsNoTracking()
            .Where(summary => summary.GroupCode == "HF-7QK2")
            .ToListAsync(cancellationToken));
        decimal afterPerParticipant = afterCorrection.BountyIsk / afterCorrection.ParticipantCount;

        Assert.True(unlinked.IsSuccess);
        Assert.True(rebuilt.IsSuccess);
        Assert.Equal(50m, beforePerParticipant);
        Assert.Equal(100m, afterPerParticipant);
        Assert.NotEqual(beforePerParticipant, afterPerParticipant);
        Assert.NotEqual(participantRunId, stationRunId);
    }

    [AvaloniaFact]
    public async Task LinkRunToGroupCode_ExistingDifferentCode_Fails()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal, StartedAtUtc,
            0, null, null, "HF-7QK2"), cancellationToken);

        Result linked = await dispatcher.Send(new LinkRunToGroupCodeCommand(started.Value, "HF-1A2B"), cancellationToken);

        Assert.False(linked.IsSuccess);
    }

    private sealed class FleetWireTransport : IRemoteEventTransport
    {
        public IEventBus? Destination { get; set; }

        public Task SendAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default) =>
            Destination?.PublishAsync(integrationEvent, EventTarget.Local, cancellationToken) ?? Task.CompletedTask;
    }

    private static async Task<Guid> _SaveRunAsync(IDispatcher dispatcher, long characterId, decimal bountyIsk,
        CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [new RunBountyEntryInput
            {
                OccurredAtUtc = StartedAtUtc.AddMinutes(5),
                Isk = bountyIsk
            }], [], []), cancellationToken);

        Assert.True(started.IsSuccess);
        Assert.True(saved.IsSuccess);
        return started.Value;
    }
}
