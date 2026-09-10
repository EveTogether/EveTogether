using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Runs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-222: the one place a screen hears that a run changed. What it promises every screen listening to it — a burst
/// folded into one read, on the UI thread, never two reads on one screen at once — is tested here against a real
/// window, where every screen test runs with it at zero (<see cref="TestClientInstance"/>).
/// </summary>
public sealed class RunChangeFeedTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);

    /// <summary>AC-4's shape: a pocket's worth of payouts, each its own write and its own signal, published from the
    /// thread the gamelog watch runs on. Counter-proof: a feed that hands every signal over as it comes (no window)
    /// reads the screen two hundred times here; one that forgets the UI thread fails the thread check.</summary>
    [AvaloniaFact]
    public async Task ABurstFromAnotherThread_IsOneReload_OnTheUiThread()
    {
        var bus = new InProcessEventBus();
        using var feed = new RunChangeFeed(bus, NullLogger<RunChangeFeed>.Instance, Window);
        List<RunChangeBatch> delivered = [];
        bool allOnUiThread = true;
        using IDisposable listening = feed.Subscribe(batch =>
        {
            allOnUiThread &= Dispatcher.UIThread.CheckAccess();
            delivered.Add(batch);
            return Task.CompletedTask;
        });

        Guid[] runIds = [.. Enumerable.Range(0, 200).Select(_ => Guid.NewGuid())];
        await Task.Run(async () =>
        {
            foreach (Guid runId in runIds)
                await bus.PublishAsync(new RunsChangedEvent(runId, "HF-7QK2"));
        }, TestContext.Current.CancellationToken);
        await ActivityWindowHarness.WaitUntil(() => delivered.Count > 0);
        await Task.Delay(Window * 3, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        RunChangeBatch batch = Assert.Single(delivered);
        Assert.True(allOnUiThread);
        Assert.Equal(runIds.Length, batch.RunIds.Count);
        Assert.True(batch.Concerns([runIds[^1]], null));
        Assert.True(batch.Concerns([], "HF-7QK2"));
        Assert.False(batch.IsUnscoped);
    }

    /// <summary>A change landing while a screen is still reading the last one is kept for it and handed over when it
    /// finishes — once, however many changes that was. Counter-proof: a feed that calls a listener regardless starts
    /// a second read on top of the first, which is what <c>overlapping</c> catches.</summary>
    [AvaloniaFact]
    public async Task ChangesArrivingDuringAReload_AreHandedOverOnce_AfterItFinishes()
    {
        var bus = new InProcessEventBus();
        using var feed = new RunChangeFeed(bus, NullLogger<RunChangeFeed>.Instance, TimeSpan.Zero);
        var firstRead = new TaskCompletionSource();
        List<RunChangeBatch> delivered = [];
        int reading = 0;
        bool overlapping = false;
        using IDisposable listening = feed.Subscribe(async batch =>
        {
            overlapping |= ++reading > 1;
            delivered.Add(batch);
            if (delivered.Count == 1)
                await firstRead.Task;
            reading--;
        });

        await bus.PublishAsync(new RunsChangedEvent(Guid.NewGuid()));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(delivered);

        Guid second = Guid.NewGuid();
        Guid third = Guid.NewGuid();
        await bus.PublishAsync(new RunsChangedEvent(second));
        Dispatcher.UIThread.RunJobs();
        await bus.PublishAsync(new RunsChangedEvent(third));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(delivered);

        firstRead.SetResult();
        await ActivityWindowHarness.WaitUntil(() => delivered.Count == 2);

        Assert.Equal(2, delivered.Count);
        Assert.False(overlapping);
        Assert.True(delivered[1].RunIds.ToHashSet().SetEquals([second, third]));
    }

    /// <summary>A change that named no run — a rebuild of every summary — concerns every screen, whatever it
    /// shows.</summary>
    [AvaloniaFact]
    public async Task AChangeNamingNoRun_ConcernsEveryScreen()
    {
        var bus = new InProcessEventBus();
        using var feed = new RunChangeFeed(bus, NullLogger<RunChangeFeed>.Instance, TimeSpan.Zero);
        RunChangeBatch? delivered = null;
        using IDisposable listening = feed.Subscribe(batch =>
        {
            delivered = batch;
            return Task.CompletedTask;
        });

        await bus.PublishAsync(new RunsChangedEvent(null));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(delivered);
        Assert.True(delivered.IsUnscoped);
        Assert.True(delivered.Concerns([Guid.NewGuid()], null));
    }

    /// <summary>A screen that has been closed hears nothing more, even for a change already on its way.</summary>
    [AvaloniaFact]
    public async Task ADisposedSubscription_HearsNothingMore()
    {
        var bus = new InProcessEventBus();
        using var feed = new RunChangeFeed(bus, NullLogger<RunChangeFeed>.Instance, TimeSpan.Zero);
        int reloads = 0;
        IDisposable listening = feed.Subscribe(_ =>
        {
            reloads++;
            return Task.CompletedTask;
        });

        await bus.PublishAsync(new RunsChangedEvent(Guid.NewGuid()));
        listening.Dispose();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, reloads);
    }
}
