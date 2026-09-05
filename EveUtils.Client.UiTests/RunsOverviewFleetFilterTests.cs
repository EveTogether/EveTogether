using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-185: RUNS on a finished fleet row opens this same screen scoped to that fleet — filtered to only its
/// own activity (via <c>GetActivityOverviewQuery</c>'s FleetId), and honest about what an empty result means when it
/// is one (via <c>GetFleetRunCoverageQuery</c>), never a silent "0".</summary>
public sealed class RunsOverviewFleetFilterTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyList<Character> NoCrew = [];

    [AvaloniaFact]
    public async Task Filtered_ShowsOnlyThatFleetsActivity_AndNamesItInTheHeader()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _StartAndSaveAsync(dispatcher, 90000001, fleetId: 42, cancellationToken);
        await _StartAndSaveAsync(dispatcher, 90000002, fleetId: 99, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        var vm = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services, NoCrew,
            runClock: false, fleetFilter: new RunsFleetFilter(42, "Wednesday Homefronts", StartedAtUtc.AddDays(-1)));
        await vm.LoadAsync(cancellationToken);

        Assert.Equal("Runs for 'Wednesday Homefronts'", vm.FleetFilterText);
        Assert.Single(vm.Tabs[0].Days.SelectMany(day => day.Rows));
        Assert.Null(vm.Tabs[0].StatusMessage);
    }

    [AvaloniaFact]
    public async Task Filtered_EmptyAndYoungerThanTracking_ReportsARealZero()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // Puts a floor in RunGroupOrigin without touching fleet 42 itself.
        await _StartAndSaveAsync(dispatcher, 90000001, fleetId: 99, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        // RunGroupOrigin stamps RecordedAtUtc off the wall clock, not off the simulated StartedAtUtc — so "younger
        // than the floor" has to be measured against real now, not against the fixture's fictional date.
        var vm = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services, NoCrew,
            runClock: false, fleetFilter: new RunsFleetFilter(42, "Brand New Fleet", DateTime.UtcNow.AddMinutes(1)));
        await vm.LoadAsync(cancellationToken);

        Assert.Empty(vm.Tabs[0].Days);
        Assert.Equal("'Brand New Fleet' has no completed runs on record.", vm.Tabs[0].StatusMessage);
    }

    [AvaloniaFact]
    public async Task Filtered_EmptyAndOlderThanTracking_ReportsUnknownRatherThanZero()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _StartAndSaveAsync(dispatcher, 90000001, fleetId: 99, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        var vm = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services, NoCrew,
            runClock: false, fleetFilter: new RunsFleetFilter(42, "Old Fleet", StartedAtUtc.AddDays(-30)));
        await vm.LoadAsync(cancellationToken);

        Assert.Empty(vm.Tabs[0].Days);
        Assert.DoesNotContain("no completed runs on record", vm.Tabs[0].StatusMessage);
        Assert.Contains("before this client tracked", vm.Tabs[0].StatusMessage);
    }

    private static async Task _StartAndSaveAsync(IDispatcher dispatcher, long characterId, long fleetId, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, FleetId: fleetId, IsFleetCommander: true), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
    }
}
