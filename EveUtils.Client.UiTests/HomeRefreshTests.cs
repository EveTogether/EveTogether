using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Fleet;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Home;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-324: the home updates per block, never as a whole, and reads nothing on the UI thread. Neither is
/// visible on a render — a home that rebuilds itself twelve times a second looks exactly like one that does not, until
/// it hangs the way the run window did in ET-298 — so the storm is played here and counted.</summary>
public sealed class HomeRefreshTests
{
    private const long CharacterId = 90000001;

    /// <summary>A storm of three seconds — twenty run changes, ten roster changes and fifty fleet metrics a second —
    /// costs a handful of reads per block per second, fleet metrics none at all, and not one SDE query on the UI thread.
    /// The runs read folds a burst into the feed's 250 ms window and owes the rest; the fleets read lets a burst settle
    /// for 300 ms. Counter-proof: a block that read per event would show sixty runs reads and thirty fleet reads.</summary>
    [AvaloniaFact]
    public async Task AStormOfRunAndFleetEvents_CostsAFewReadsPerBlock_AndNoSdeOnTheUiThread()
    {
        var sde = SdeCallCounter.Wrap(new FakeSdeAccessor()
            .Add(17715, "Gila", 26, 6)
            .AddSolarSystem(new SdeSolarSystem(30002187, "Amarr", 1.0)));
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton(sde);
            services.AddSingleton(provider => new RunChangeFeed(provider.GetRequiredService<IEventBus>(),
                provider.GetRequiredService<ILogger<RunChangeFeed>>(), RunChangeFeed.DefaultWindow));
        });
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Noahmarr", (int)CharacterId), TestContext.Current.CancellationToken);
        for (int run = 0; run < 8; run++)
            await _SaveRunAsync(dispatcher, DateTime.UtcNow.AddHours(-run - 1));

        using var home = new HomeDashboardViewModel(instance.Services, HomeNavigation.None, []);
        await home.LoadAsync();
        int runsBefore = home.RunsReadCount;
        int fleetsBefore = home.Fleets.ReadCount;
        SdeCallCounter.Reset();

        IEventBus bus = instance.Services.GetRequiredService<IEventBus>();
        IFleetRosterWatch roster = instance.Services.GetRequiredService<IFleetRosterWatch>();
        TimeSpan storm = TimeSpan.FromSeconds(3);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int tick = 0; clock.Elapsed < storm; tick++)
        {
            await bus.PublishAsync(new FleetMetricEvent(new MetricSample((int)CharacterId, 42, MetricKind.Dps, tick, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
            if (tick % 2 == 0)
                await bus.PublishAsync(new RunsChangedEvent(Guid.NewGuid()));
            if (tick % 5 == 0)
                roster.Announce(FleetRosterChange.Reloaded(42));
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        // Let the last owed reads land before counting.
        for (int settle = 0; settle < 40; settle++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        double seconds = clock.Elapsed.TotalSeconds;
        double runsPerSecond = (home.RunsReadCount - runsBefore) / seconds;
        double fleetsPerSecond = (home.Fleets.ReadCount - fleetsBefore) / seconds;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"storm {seconds:0.0}s: runs reads {runsPerSecond:0.0}/s, fleet reads {fleetsPerSecond:0.0}/s, " +
            $"SDE on UI thread {SdeCallCounter.OnUiThread}, SDE off it {SdeCallCounter.OffUiThread}");

        Assert.InRange(runsPerSecond, 0.3, 5);
        Assert.InRange(fleetsPerSecond, 0.3, 4);
        Assert.Equal(0, SdeCallCounter.OnUiThread);
    }

    /// <summary>Fleet metrics are the ET-298 storm itself — twelve a second on a six-pilot fleet. The home has no business
    /// with them: a burst of them alone reads nothing.</summary>
    [AvaloniaFact]
    public async Task FleetMetricsAlone_ReadNothing()
    {
        using var instance = TestClientInstance.Create();
        using var home = new HomeDashboardViewModel(instance.Services, HomeNavigation.None, []);
        await home.LoadAsync();
        int runsBefore = home.RunsReadCount;
        int fleetsBefore = home.Fleets.ReadCount;

        IEventBus bus = instance.Services.GetRequiredService<IEventBus>();
        for (int sample = 0; sample < 100; sample++)
            await bus.PublishAsync(new FleetMetricEvent(new MetricSample((int)CharacterId, 42, MetricKind.Dps, sample, sample)));
        for (int settle = 0; settle < 20; settle++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Equal(runsBefore, home.RunsReadCount);
        Assert.Equal(fleetsBefore, home.Fleets.ReadCount);
    }

    private static async Task _SaveRunAsync(IDispatcher dispatcher, DateTime startedAtUtc)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(CharacterId, ActivityKind.Site, startedAtUtc, 1234, "Homefront", 30002187));
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16), [],
            [new RunBountyEntryInput { OccurredAtUtc = startedAtUtc.AddMinutes(5), Isk = 1_000_000m }], [], []));
        await dispatcher.Send(new RebuildActivitySummariesCommand());
    }

    /// <summary>Every SDE call, split by whether it ran on the UI thread — the ET-298 hang was thousands of these on it.</summary>
    public class SdeCallCounter : DispatchProxy
    {
        private static int _onUiThread;
        private static int _offUiThread;

        private ISdeAccessor? _inner;

        public static int OnUiThread => _onUiThread;

        public static int OffUiThread => _offUiThread;

        public static ISdeAccessor Wrap(ISdeAccessor inner)
        {
            ISdeAccessor proxy = Create<ISdeAccessor, SdeCallCounter>();
            ((SdeCallCounter)(object)proxy)._inner = inner;
            return proxy;
        }

        public static void Reset()
        {
            _onUiThread = 0;
            _offUiThread = 0;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Dispatcher.UIThread.CheckAccess())
                Interlocked.Increment(ref _onUiThread);
            else
                Interlocked.Increment(ref _offUiThread);
            return targetMethod?.Invoke(_inner, args);
        }
    }
}
