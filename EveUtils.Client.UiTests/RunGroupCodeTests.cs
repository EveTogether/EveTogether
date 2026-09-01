using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Grouping;
using EveUtils.Shared.Modules.Fleet.Events;
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
        string code = RunGroupCode.Create(ActivityKind.Site);

        Assert.Matches("^HF-[A-Z0-9]{4}$", code);
    }

    [Fact]
    public void AbyssalStarts_SameSecond_ConvergeOnOneCode()
    {
        RunGroupCodeCandidate firstClient = new("HF-7QK2", StartedAtUtc.AddMilliseconds(100));
        RunGroupCodeCandidate secondClient = new("HF-1A2B", StartedAtUtc.AddMilliseconds(900));

        string code = RunGroupCodeArbiter.Select(ActivityKind.Abyssal, [firstClient, secondClient]);

        Assert.Equal("HF-7QK2", code);
    }

    [Fact]
    public void AbyssalStarts_SameMillisecond_ConvergeDeterministically()
    {
        RunGroupCodeCandidate firstClient = new("HF-7QK2", StartedAtUtc);
        RunGroupCodeCandidate secondClient = new("HF-1A2B", StartedAtUtc);

        string code = RunGroupCodeArbiter.Select(ActivityKind.Abyssal, [firstClient, secondClient]);

        Assert.Equal("HF-1A2B", code);
    }

    [Fact]
    public void HomefrontStarts_UsesCommanderCode()
    {
        RunGroupCodeCandidate commander = new("HF-7QK2", StartedAtUtc, true);
        RunGroupCodeCandidate member = new("HF-1A2B", StartedAtUtc.AddMilliseconds(-1));

        string code = RunGroupCodeArbiter.Select(ActivityKind.Site, [member, commander]);

        Assert.Equal("HF-7QK2", code);
    }

    [AvaloniaFact]
    public async Task AbyssalStarts_SameSecond_OnTwoClients_ConvergeOnOneCode()
    {
        using var firstClient = TestClientInstance.Create();
        using var secondClient = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IDispatcher firstDispatcher = firstClient.Services.GetRequiredService<IDispatcher>();
        IDispatcher secondDispatcher = secondClient.Services.GetRequiredService<IDispatcher>();

        Task<Result<Guid>> firstStarted = firstDispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal,
            StartedAtUtc.AddMilliseconds(100), 0, null, null), cancellationToken);
        Task<Result<Guid>> secondStarted = secondDispatcher.Send(new StartRunCommand(90000002, ActivityKind.Abyssal,
            StartedAtUtc.AddMilliseconds(900), 0, null, null), cancellationToken);
        Result<Guid>[] started = await Task.WhenAll(firstStarted, secondStarted);
        string groupCode = RunGroupCodeArbiter.Select(ActivityKind.Abyssal,
            [new RunGroupCodeCandidate("HF-7QK2", StartedAtUtc.AddMilliseconds(100)),
             new RunGroupCodeCandidate("HF-1A2B", StartedAtUtc.AddMilliseconds(900))]);

        Result[] linked = await Task.WhenAll(
            firstDispatcher.Send(new LinkRunToGroupCodeCommand(started[0].Value, groupCode), cancellationToken),
            secondDispatcher.Send(new LinkRunToGroupCodeCommand(started[1].Value, groupCode), cancellationToken));

        Assert.All(started, result => Assert.True(result.IsSuccess));
        Assert.All(linked, result => Assert.True(result.IsSuccess));
        await using ClientDbContext firstDb = await firstClient.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        await using ClientDbContext secondDb = await secondClient.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Assert.Equal("HF-7QK2", Assert.Single(await firstDb.Set<Run>().ToListAsync(cancellationToken)).GroupCode);
        Assert.Equal("HF-7QK2", Assert.Single(await secondDb.Set<Run>().ToListAsync(cancellationToken)).GroupCode);
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
    public async Task PublishRunGroupCode_UsesFleetWireEvent()
    {
        using var instance = TestClientInstance.Create();
        IEventBus eventBus = instance.Services.GetRequiredService<IEventBus>();
        FleetRunGroupCodePublisher publisher = instance.Services.GetRequiredService<FleetRunGroupCodePublisher>();
        FleetRunGroupCodeEvent? received = null;
        using IDisposable subscription = eventBus.Subscribe<FleetRunGroupCodeEvent>((integrationEvent, _) =>
        {
            received = integrationEvent;
            return Task.CompletedTask;
        });

        await publisher.PublishAsync(90000001, 42, ActivityKind.Abyssal, "AB-7QK2", StartedAtUtc,
            TestContext.Current.CancellationToken);

        Assert.Equal("fleet.run-group", received?.EventType);
        Assert.Equal(42, (received as IFleetScopedEvent)?.FleetId);
        Assert.Equal("AB-7QK2", received?.Data.GroupCode);
    }
}
