using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
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

/// <summary>ET-182: a run only ever carried its <c>GroupCode</c>, and that code does not carry the fleet-id it was
/// made from — so a fleet row had no way to find its own runs without decomposing the code (the thing ET-136
/// deliberately ruled out). These tests prove the other route: <c>RunGroupOrigin</c> records a code's fleet the
/// moment both are known, and <c>GetActivityOverviewQuery</c> can filter on it without a second query path.</summary>
public sealed class RunGroupOriginTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public async Task Overview_FilteredByFleet_ReturnsOnlyThatFleetsActivity()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _StartAndSaveAsync(dispatcher, 90000001, fleetId: 42, cancellationToken);
        await _StartAndSaveAsync(dispatcher, 90000002, fleetId: 99, cancellationToken);
        await _StartAndSaveAsync(dispatcher, 90000003, fleetId: null, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> unfiltered =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> filtered =
            await dispatcher.Query(new GetActivityOverviewQuery(FleetId: 42), cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> unknownFleet =
            await dispatcher.Query(new GetActivityOverviewQuery(FleetId: 7), cancellationToken);

        Assert.Equal(3, _Value(unfiltered).Count);
        ActivityOverviewRowDto row = Assert.Single(_Value(filtered));
        Assert.NotNull(row.GroupCode);
        Assert.Empty(_Value(unknownFleet));
    }

    /// <summary>The exact shape ET-166's stop dialog and ET-170's overview both hit: a site member who is not the
    /// fleet commander starts with no group code of their own (<c>RunGroupCodeArbiter.TakesGroupFromCommanderOnly</c>)
    /// and only gets one afterwards, through the arbiter's reconciliation — <c>LinkRunToGroupCodeCommand</c>, never
    /// <c>StartRunCommand</c>. The fleet filter has to see that run too, on the member's own client.</summary>
    [AvaloniaFact]
    public async Task Overview_FilteredByFleet_IncludesARunLinkedByReconciliationNotByStart()
    {
        var commanderWire = new SingleDestinationTransport();
        var memberWire = new SingleDestinationTransport();
        using var commanderClient = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(commanderWire));
        using var memberClient = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(memberWire));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        commanderWire.Destination = memberClient.Services.GetRequiredService<IEventBus>();
        memberWire.Destination = commanderClient.Services.GetRequiredService<IEventBus>();
        _ = commanderClient.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();
        _ = memberClient.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();
        IDispatcher commanderDispatcher = commanderClient.Services.GetRequiredService<IDispatcher>();
        IDispatcher memberDispatcher = memberClient.Services.GetRequiredService<IDispatcher>();

        Result<Guid> commanderRun = await commanderDispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site,
            StartedAtUtc, 1234, "Homefront", 30000142, FleetId: 42, IsFleetCommander: true), cancellationToken);
        Result<Guid> memberRun = await memberDispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site,
            StartedAtUtc.AddSeconds(5), 1234, "Homefront", 30000142, FleetId: 42), cancellationToken);
        Assert.True(commanderRun.IsSuccess);
        Assert.True(memberRun.IsSuccess);
        // The member's own run must have picked up the commander's code before this proves anything about the
        // fleet filter — otherwise a passing assert below would just mean nothing runs.
        await using (ClientDbContext memberDb = await memberClient.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken))
        {
            Run stored = Assert.Single(await memberDb.Set<Run>().ToListAsync(cancellationToken));
            Assert.NotNull(stored.GroupCode);
        }
        await memberDispatcher.Send(new SaveRunCommand(memberRun.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        await memberDispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await memberDispatcher.Query(new GetActivityOverviewQuery(FleetId: 42), cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> wrongFleet =
            await memberDispatcher.Query(new GetActivityOverviewQuery(FleetId: 99), cancellationToken);

        Assert.Single(_Value(overview));
        Assert.Empty(_Value(wrongFleet));
    }

    /// <summary>A code is minted for one fleet; it never gets reassigned to another. The first fleet-id recorded
    /// for a code is the one that stands, so a later, unrelated call carrying a different fleet-id for the same
    /// code (which should not happen, but must not corrupt the record if it did) is a no-op.</summary>
    [AvaloniaFact]
    public async Task RunGroupOrigin_KeepsTheFirstFleetIdRecordedForACode()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal, StartedAtUtc, 0, null, null,
            GroupCode: "HF-7QK2", FleetId: 42), cancellationToken);

        await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Abyssal, StartedAtUtc.AddMinutes(1), 0, null, null,
            GroupCode: "HF-7QK2", FleetId: 99), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        RunGroupOrigin origin = Assert.Single(await db.Set<RunGroupOrigin>().ToListAsync(cancellationToken));
        Assert.Equal(42, origin.FleetId);
    }

    private static async Task _StartAndSaveAsync(IDispatcher dispatcher, long characterId, long? fleetId, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, FleetId: fleetId, IsFleetCommander: fleetId is not null), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
    }

    private static T _Value<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Messages[0].Text);
        return result.Value!;
    }

    /// <summary>One client sending straight to another — the minimal shape needed here, unlike
    /// <c>RunGroupCodeTests.FleetWireTransport</c>'s multi-destination fixture for a three-pilot fleet.</summary>
    private sealed class SingleDestinationTransport : IRemoteEventTransport
    {
        public IEventBus? Destination { get; set; }

        public async Task SendAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            if (Destination is { } destination)
                await destination.PublishAsync(integrationEvent, EventTarget.Local, cancellationToken);
        }
    }
}
