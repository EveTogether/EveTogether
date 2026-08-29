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
/// ET-56: the gamelog sees a pilot enter the abyss but never leave — you come out where you fired the filament and
/// nothing is written there. ESI is what ends a run, and these cover the endings: seen outside, a false start taken
/// back, and the two refusals no retry can fix.
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

    [Fact]
    public async Task SeenOutside_EndsTheRun_AndRecordsWhenWeSawIt()
    {
        var locations = new FakeLocationClient(AbyssalRoom, AbyssalRoom, Aphend);
        var monitor = Build(locations, out _);

        DateTime? endedWith = null;
        var ended = false;
        var before = DateTime.UtcNow;

        await monitor.WatchAsync(1, seen => { endedWith = seen; ended = true; }, CancellationToken.None);

        Assert.True(ended);
        Assert.Equal(3, locations.Calls);
        // Not just "cleared": the sighting is the anchor a follow-up run has to use, since a second filament is fired
        // in space and writes no location line.
        Assert.NotNull(endedWith);
        Assert.InRange(endedWith!.Value, before, DateTime.UtcNow);
    }

    /// <summary>
    /// The gamelog's name list is deliberately short, so a normal-space fight can open a run that never happened.
    /// Before ESI there was no way to notice; now the first poll takes it back.
    /// </summary>
    [Fact]
    public async Task AFalseStartFromTheGamelog_IsTakenBackByTheFirstPoll()
    {
        var locations = new FakeLocationClient(Aphend);
        var monitor = Build(locations, out _);

        var ended = false;
        await monitor.WatchAsync(1, _ => ended = true, CancellationToken.None);

        Assert.True(ended);
        Assert.Equal(1, locations.Calls);
    }

    [Fact]
    public async Task WhileStillInside_TheRunIsLeftAlone()
    {
        using var cts = new CancellationTokenSource();
        var locations = new FakeLocationClient(AbyssalRoom, AbyssalRoom) { CancelAfter = (2, cts) };
        var monitor = Build(locations, out _);

        var ended = false;
        await monitor.WatchAsync(1, _ => ended = true, cts.Token);

        Assert.False(ended);
    }

    /// <summary>
    /// No scope means no way to see the pilot come out, so the countdown will run to its end. That is worth saying out
    /// loud, and the toast carries the one action that fixes it — which is also why it must not auto-dismiss.
    /// </summary>
    [Fact]
    public async Task WithoutTheLocationScope_ItStopsAndOffersToFixIt()
    {
        var locations = new FakeLocationClient { Error = EsiErrorKind.ScopeMissing };
        var monitor = Build(locations, out var toasts);

        await monitor.WatchAsync(1, _ => { }, CancellationToken.None);

        var toast = Assert.Single(toasts.ActionToasts);
        Assert.Equal("No abyssal detection", toast.Title);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Contains(toast.Actions, a => a.Style == ToastActionStyle.Affirmative);

        // One call, not a poll loop: the pre-flight refuses a missing scope without sending anything, and repeating it
        // would only burn ESI budget.
        Assert.Equal(1, locations.Calls);
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
    /// costs a ship. Only a refusal no retry can fix stops the watch.
    /// </summary>
    [Fact]
    public async Task ATransientFailure_DoesNotStopTheWatch()
    {
        var locations = new FakeLocationClient(AbyssalRoom, Aphend) { FailFirst = EsiErrorKind.ServerError };
        var monitor = Build(locations, out var toasts);

        var ended = false;
        await monitor.WatchAsync(1, _ => ended = true, CancellationToken.None);

        Assert.True(ended);
        Assert.Empty(toasts.Toasts);
    }

    private static AbyssalLocationMonitor Build(FakeLocationClient locations, out RecordingToastService toasts)
    {
        toasts = new RecordingToastService();
        return new AbyssalLocationMonitor(locations, toasts, new ServiceCollection().BuildServiceProvider(),
            NullLogger<AbyssalLocationMonitor>.Instance)
        {
            // The real values are 6 s and 20 minutes; the logic under test is the same at these.
            PollInterval = TimeSpan.FromMilliseconds(1),
            WatchTimeout = TimeSpan.FromMilliseconds(200),
        };
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
