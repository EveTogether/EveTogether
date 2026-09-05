using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-185: the question <c>GetActivityOverviewQuery</c>'s FleetId filter cannot answer on its own — whether
/// an empty result means "this fleet flew nothing" or "this fleet predates RunGroupOrigin" (ET-182). Three shapes:
/// a fleet with completed runs on record, a fleet confirmed too young to have missed the record, and a fleet old
/// enough (or a client with no record at all) that the gap cannot be ruled out.</summary>
public sealed class GetFleetRunCoverageQueryTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public async Task Fleet_WithCompletedRuns_ReportsTheCountAsKnown()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _StartAndSaveAsync(dispatcher, 90000001, fleetId: 42, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Result<FleetRunCoverageDto> coverage = await dispatcher.Query(
            new GetFleetRunCoverageQuery(42, StartedAtUtc.AddDays(-1)), cancellationToken);

        Assert.True(coverage.IsSuccess);
        Assert.Equal(new FleetRunCoverageDto(1, IsKnown: true), coverage.Value);
    }

    /// <summary>Nothing recorded for this fleet, but the fleet itself came into being after the oldest row
    /// RunGroupOrigin holds — so it could not have flown a run RunGroupOrigin would have missed. Zero is real.</summary>
    [AvaloniaFact]
    public async Task Fleet_YoungerThanTheOldestOrigin_ReportsAKnownZero()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // Puts a row in RunGroupOrigin so the table is not empty, floor = StartedAtUtc.
        await _StartAndSaveAsync(dispatcher, 90000001, fleetId: 99, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        // RunGroupOrigin stamps RecordedAtUtc off the wall clock, not off the simulated StartedAtUtc — so "younger
        // than the floor" has to be measured against real now, not against the fixture's fictional date.
        Result<FleetRunCoverageDto> coverage = await dispatcher.Query(
            new GetFleetRunCoverageQuery(42, DateTime.UtcNow.AddMinutes(1)), cancellationToken);

        Assert.True(coverage.IsSuccess);
        Assert.Equal(new FleetRunCoverageDto(0, IsKnown: true), coverage.Value);
    }

    /// <summary>Nothing recorded for this fleet, and the fleet predates the oldest row on file — a fleet from
    /// before ET-182 could easily have flown runs RunGroupOrigin never saw. Zero here would be a guess.</summary>
    [AvaloniaFact]
    public async Task Fleet_OlderThanTheOldestOrigin_ReportsAnUnknownZero()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _StartAndSaveAsync(dispatcher, 90000001, fleetId: 99, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Result<FleetRunCoverageDto> coverage = await dispatcher.Query(
            new GetFleetRunCoverageQuery(42, StartedAtUtc.AddMinutes(-5)), cancellationToken);

        Assert.True(coverage.IsSuccess);
        Assert.Equal(new FleetRunCoverageDto(0, IsKnown: false), coverage.Value);
    }

    /// <summary>An empty client: RunGroupOrigin has never recorded anything at all, so there is no floor to compare
    /// against — the same "cannot rule it out" answer as a fleet older than the oldest row.</summary>
    [AvaloniaFact]
    public async Task Fleet_OnAClientWithNoRecordedOrigins_ReportsAnUnknownZero()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        Result<FleetRunCoverageDto> coverage = await dispatcher.Query(
            new GetFleetRunCoverageQuery(42, StartedAtUtc), TestContext.Current.CancellationToken);

        Assert.True(coverage.IsSuccess);
        Assert.Equal(new FleetRunCoverageDto(0, IsKnown: false), coverage.Value);
    }

    private static async Task _StartAndSaveAsync(IDispatcher dispatcher, long characterId, long fleetId, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, FleetId: fleetId, IsFleetCommander: true), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
    }
}
