using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Client.Notifications;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-62: ESI is the only source that can see either end of an abyssal run — a filament writes nothing when it pulls
/// you in, and you leave where you fired it. So the watch runs for the whole session and reports every reading, and
/// these cover what it reports: inside, outside, and the refusals that mean it can report nothing at all.
/// </summary>
public class AbyssalLocationMonitorTests
{
    private const int Aphend = 30002718;      // an ordinary high-sec system id
    private const int AbyssalRoom = 32000042; // inside ADR01's range

    [Fact]
    public void TheAbyssalRange_IsClosed_SoTheProvingGroundsAreNotAbyssal()
    {
        // [gemeten, live ESI 2026-08-29] exactly 32000001-32000200, and the ids just outside do not exist.
        Assert.True(AbyssalSpace.IsAbyssalSystem(32000001));
        Assert.True(AbyssalSpace.IsAbyssalSystem(32000200));
        Assert.False(AbyssalSpace.IsAbyssalSystem(32000000));
        Assert.False(AbyssalSpace.IsAbyssalSystem(32000201));

        // The counter-proof the ticket asks for: an open ">= 32000001" would swallow both of these. VR-01..05 are the
        // Proving Grounds and GPMR-01 is its own thing; neither is abyssal deadspace.
        Assert.False(AbyssalSpace.IsAbyssalSystem(34000001));
        Assert.False(AbyssalSpace.IsAbyssalSystem(34000200));
        Assert.False(AbyssalSpace.IsAbyssalSystem(36000001));

        Assert.False(AbyssalSpace.IsAbyssalSystem(Aphend));
    }

    /// <summary>Every reading is reported, so the entry and the exit are both observed rather than inferred.</summary>
    [Fact]
    public async Task BothEndsOfARun_AreReported()
    {
        using var cts = new CancellationTokenSource();
        var locations = new FakeLocationClient(Aphend, AbyssalRoom, AbyssalRoom, Aphend) { CancelAfter = (4, cts) };
        var monitor = Build(locations, out _);

        var seen = new List<bool?>();
        await monitor.WatchAsync(1, reading => seen.Add(reading.Inside), cts.Token);

        Assert.Equal([false, true, true, false], seen);
    }

    /// <summary>
    /// The regression ET-62 exists for. The old watch stopped the moment it saw the pilot outside, so nothing looked
    /// at them again until a run was already under way — and the countdown then anchored on however long ago that
    /// was. Measured 2026-08-29: 46 minutes, and the clock read "--:--" for the whole run.
    /// </summary>
    [Fact]
    public async Task SeeingThemOutside_DoesNotStopTheWatch()
    {
        using var cts = new CancellationTokenSource();
        var locations = new FakeLocationClient(Aphend, Aphend, Aphend, AbyssalRoom) { CancelAfter = (4, cts) };
        var monitor = Build(locations, out _);

        var seen = new List<bool?>();
        await monitor.WatchAsync(1, reading => seen.Add(reading.Inside), cts.Token);

        // The old behaviour ended here after one call, with `seen` holding a single entry.
        Assert.Equal(4, locations.Calls);
        Assert.Equal(true, seen[^1]);
    }

    /// <summary>The sighting time is what the next run anchors on, so it has to be the moment of the reading.</summary>
    [Fact]
    public async Task EachReading_IsStampedWhenItWasTaken()
    {
        using var cts = new CancellationTokenSource();
        var locations = new FakeLocationClient(Aphend, AbyssalRoom) { CancelAfter = (2, cts) };
        var monitor = Build(locations, out _);

        var before = DateTime.UtcNow;
        var stamps = new List<DateTime>();
        await monitor.WatchAsync(1, reading => stamps.Add(reading.AtUtc), cts.Token);

        Assert.Equal(2, stamps.Count);
        Assert.All(stamps, at => Assert.InRange(at, before, DateTime.UtcNow));
    }

    /// <summary>
    /// No scope means no abyssal detection at all now — there is no gamelog fallback left. That is worth saying out
    /// loud, and the toast carries the one action that fixes it, which is also why it must not auto-dismiss.
    /// </summary>
    [Fact]
    public async Task WithoutTheLocationScope_ItStopsAndOffersToFixIt()
    {
        var locations = new FakeLocationClient { Error = EsiErrorKind.ScopeMissing };
        var monitor = Build(locations, out var toasts);

        var seen = new List<bool?>();
        await monitor.WatchAsync(1, reading => seen.Add(reading.Inside), CancellationToken.None);

        var toast = Assert.Single(toasts.ActionToasts);
        Assert.Equal("No abyssal detection", toast.Title);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Contains(toast.Actions, a => a.Style == ToastActionStyle.Affirmative);

        // One call, not a poll loop: the pre-flight refuses a missing scope without sending anything, and repeating it
        // would only burn ESI budget. And the clock is cleared rather than left frozen on its last anchor.
        Assert.Equal(1, locations.Calls);
        Assert.Equal([null], seen);
    }

    [Fact]
    public async Task TheScopeWarning_IsNotRepeatedForTheSameCharacter()
    {
        var locations = new FakeLocationClient { Error = EsiErrorKind.ScopeMissing };
        var monitor = Build(locations, out var toasts);

        for (var run = 0; run < 3; run++)
            await monitor.WatchAsync(1, _ => { }, CancellationToken.None);

        Assert.Single(toasts.ActionToasts);
    }

    /// <summary>
    /// A 5xx or a timeout says nothing about where the pilot is, and dropping the clock on one is the failure that
    /// costs a ship. Only a refusal no retry can fix stops the watch immediately.
    /// </summary>
    [Fact]
    public async Task ATransientFailure_DoesNotStopTheWatch()
    {
        using var cts = new CancellationTokenSource();
        var locations = new FakeLocationClient(AbyssalRoom, Aphend) { FailFirst = EsiErrorKind.ServerError, CancelAfter = (3, cts) };
        var monitor = Build(locations, out var toasts);

        var seen = new List<bool?>();
        await monitor.WatchAsync(1, reading => seen.Add(reading.Inside), cts.Token);

        Assert.Equal([true, false], seen);
        Assert.Empty(toasts.Toasts);
    }

    /// <summary>Unbroken failure is different: a watch that can read nothing must not leave a clock standing.</summary>
    [Fact]
    public async Task UnbrokenFailure_GivesUpAndClearsTheClock()
    {
        var locations = new FakeLocationClient { Error = EsiErrorKind.ServerError };
        var monitor = Build(locations, out _);

        var seen = new List<bool?>();
        await monitor.WatchAsync(1, reading => seen.Add(reading.Inside), CancellationToken.None);

        Assert.Equal([null], seen);
        Assert.InRange(locations.Calls, 20, 22);
    }

    private static AbyssalLocationMonitor Build(FakeLocationClient locations, out RecordingToastService toasts)
    {
        toasts = new RecordingToastService();
        var monitor = new AbyssalLocationMonitor(locations, toasts, new ServiceCollection().BuildServiceProvider(),
            NullLogger<AbyssalLocationMonitor>.Instance)
        {
            // The real value is 6 s; the logic under test is the same at this one.
            PollInterval = TimeSpan.FromMilliseconds(1),
        };
        monitor.UiReady();
        return monitor;
    }

    /// <summary>
    /// Regression: the watch starts while the app is still booting, and a character without the scope refuses on its
    /// first poll and raises a toast. Reading Avalonia's dispatcher before the UI thread owns it binds it to the
    /// wrong thread, and the app's own start-up then dies on VerifyAccess — measured 2026-08-30, no window at all.
    /// </summary>
    [Fact]
    public async Task NoPollHappens_BeforeTheUiIsReady()
    {
        var locations = new FakeLocationClient(Aphend);
        var monitor = new AbyssalLocationMonitor(locations, new RecordingToastService(),
            new ServiceCollection().BuildServiceProvider(), NullLogger<AbyssalLocationMonitor>.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
        };

        using var cts = new CancellationTokenSource();
        var watching = monitor.WatchAsync(1, _ => { }, cts.Token);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, locations.Calls);   // still gated

        monitor.UiReady();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await watching;

        Assert.True(locations.Calls > 0, "the watch must start once the UI is ready");
    }

    private sealed class FakeLocationClient(params int[] systems) : IEsiLocationClient
    {
        public int Calls { get; private set; }
        public EsiErrorKind? Error { get; init; }
        public EsiErrorKind? FailFirst { get; init; }
        public (int After, CancellationTokenSource Source)? CancelAfter { get; init; }

        private readonly Queue<int> _systems = new(systems);

        public Task<EsiResult<EsiCharacterLocation>> GetLocationAsync(int characterId, CancellationToken cancellationToken = default)
        {
            Calls++;

            if (CancelAfter is { } cancel && Calls >= cancel.After)
                cancel.Source.Cancel();

            if (Error is { } fatal)
                return Task.FromResult(EsiResult<EsiCharacterLocation>.Fail(EsiError.Of(fatal, "fake")));

            if (FailFirst is { } transient && Calls == 1)
                return Task.FromResult(EsiResult<EsiCharacterLocation>.Fail(EsiError.Of(transient, "fake")));

            var system = _systems.Count > 0 ? _systems.Dequeue() : 0;
            return Task.FromResult(EsiResult<EsiCharacterLocation>.Ok(new EsiCharacterLocation { SolarSystemId = system }));
        }
    }
}
