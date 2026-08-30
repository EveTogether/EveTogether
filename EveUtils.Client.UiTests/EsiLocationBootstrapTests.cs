using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Esi;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-63, deel 1. The gamelog only speaks when something happens: a jump or an undock writes a line, sitting still
/// writes nothing. So a pilot who was already parked where they are has no location in the app at all — right after
/// EVE Together starts, and again when a character comes online without moving. This is the one-off that closes
/// that gap from the ESI location watch ET-62 already runs, and what these cover is mostly where it stops:
/// only a gap, only outside the abyss, only once, and never over a gamelog line.
/// </summary>
public class EsiLocationBootstrapTests
{
    private const int Pilot = 90000123;
    private const string Name = "Pilot";
    private const int Jita = 30000142;
    private const int AbyssalRoom = 32000042;

    [AvaloniaFact]
    public async Task AnUnknownLocation_IsFilledFromTheEsiReading()
    {
        var monitor = new FakeMonitor();
        using var instance = Build(monitor, new FakeSystemNames { [Jita] = "Jita" });
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();

        gamelog.MapCharacter(Pilot, Name);
        Assert.Null(gamelog.Snapshot(Name).Location);   // nothing has happened in the log, so nothing is known

        monitor.Report(Pilot, Jita);

        Assert.Equal("Jita", await WaitForLocationAsync(gamelog));
    }

    /// <summary>
    /// The gap is the whole gate. A system the gamelog has already named is not asked about again — it is the
    /// faster and more direct source, and this exists to fill a hole rather than to become a second one.
    /// </summary>
    [AvaloniaFact]
    public async Task AKnownLocation_IsLeftAlone_AndCostsNoLookup()
    {
        var monitor = new FakeMonitor();
        var names = new FakeSystemNames { [Jita] = "Jita" };
        using var instance = Build(monitor, names);
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();

        gamelog.MapCharacter(Pilot, Name);
        gamelog.SetLocation(Name, "Amarr", DateTime.UtcNow);   // a jump line the watcher read

        for (var i = 0; i < 5; i++)
            monitor.Report(Pilot, Jita);
        await Task.Delay(100);

        Assert.Equal("Amarr", gamelog.Snapshot(Name).Location);
        Assert.Equal(0, names.Lookups);
    }

    /// <summary>
    /// Once filled, done. The watch keeps polling for the abyssal clock — that is why it is continuous — but the
    /// gap it was asked about is closed, so it stops costing anything. Six characters is six lookups, then nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task TheGapIsFilledOnce_NotOnEveryPoll()
    {
        var monitor = new FakeMonitor();
        var names = new FakeSystemNames { [Jita] = "Jita" };
        using var instance = Build(monitor, names);
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();

        gamelog.MapCharacter(Pilot, Name);
        monitor.Report(Pilot, Jita);
        Assert.Equal("Jita", await WaitForLocationAsync(gamelog));

        for (var i = 0; i < 20; i++)      // two minutes of polling at the real 6 s interval
            monitor.Report(Pilot, Jita);
        await Task.Delay(100);

        Assert.Equal(1, names.Lookups);
    }

    /// <summary>
    /// And after it, the gamelog is the source again. A jump the log reports overwrites what ESI seeded, and no
    /// later reading takes it back.
    /// </summary>
    [AvaloniaFact]
    public async Task TheGamelogTakesOverAgain_AfterTheGapIsFilled()
    {
        var monitor = new FakeMonitor();
        using var instance = Build(monitor, new FakeSystemNames { [Jita] = "Jita" });
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();

        gamelog.MapCharacter(Pilot, Name);
        monitor.Report(Pilot, Jita);
        Assert.Equal("Jita", await WaitForLocationAsync(gamelog));

        gamelog.SetLocation(Name, "Perimeter", DateTime.UtcNow);
        for (var i = 0; i < 5; i++)
            monitor.Report(Pilot, Jita);   // ESI still reads the cached old system for a few seconds
        await Task.Delay(100);

        Assert.Equal("Perimeter", gamelog.Snapshot(Name).Location);
    }

    /// <summary>
    /// The race the round trip opens: a jump lands while the id→name lookup is in the air. An ESI answer older
    /// than what we already know must not overwrite it, so the field is re-read after the await rather than
    /// assumed still empty.
    /// </summary>
    [AvaloniaFact]
    public async Task AJumpLandingDuringTheLookup_Wins()
    {
        var monitor = new FakeMonitor();
        var gate = new TaskCompletionSource();
        var names = new FakeSystemNames { [Jita] = "Jita", Hold = gate.Task };
        using var instance = Build(monitor, names);
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();

        gamelog.MapCharacter(Pilot, Name);
        monitor.Report(Pilot, Jita);                            // lookup starts, and blocks
        for (var i = 0; i < 100 && names.Lookups == 0; i++)
            await Task.Delay(10);
        Assert.Equal(1, names.Lookups);                         // it really is in the air, so the race is real

        gamelog.SetLocation(Name, "Perimeter", DateTime.UtcNow); // the pilot jumps while it is in the air
        gate.SetResult();
        await Task.Delay(100);

        Assert.Equal("Perimeter", gamelog.Snapshot(Name).Location);
    }

    /// <summary>
    /// A room in the abyss has no name worth showing, and the countdown owns that readout anyway. So an abyssal
    /// reading fills nothing — the gap stays open and closes on the first reading from outside, which is also the
    /// first reading that means anything.
    /// </summary>
    [AvaloniaFact]
    public async Task AnAbyssalReading_FillsNothing_ButTheOneAfterItDoes()
    {
        var monitor = new FakeMonitor();
        var names = new FakeSystemNames { [Jita] = "Jita", [AbyssalRoom] = "V-4" };
        using var instance = Build(monitor, names);
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();

        gamelog.MapCharacter(Pilot, Name);
        monitor.Report(Pilot, AbyssalRoom);
        await Task.Delay(100);

        Assert.Null(gamelog.Snapshot(Name).Location);
        Assert.Equal(0, names.Lookups);

        monitor.Report(Pilot, Jita);   // they come out
        Assert.Equal("Jita", await WaitForLocationAsync(gamelog));
    }

    /// <summary>
    /// A lost watch — no scope, no working token, ESI down for good — reports no system at all. The location stays
    /// unknown, exactly as it was before this existed. Nothing is asked, nothing is said: not granting the scope is
    /// a choice, not a fault.
    /// </summary>
    [AvaloniaFact]
    public async Task ALostWatch_FillsNothing_AndAsksNothing()
    {
        var monitor = new FakeMonitor();
        var names = new FakeSystemNames { [Jita] = "Jita" };
        using var instance = Build(monitor, names);
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();

        gamelog.MapCharacter(Pilot, Name);
        monitor.Lose(Pilot);
        await Task.Delay(100);

        Assert.Null(gamelog.Snapshot(Name).Location);
        Assert.Equal(0, names.Lookups);
    }

    /// <summary>
    /// A lookup that cannot answer leaves the location unknown rather than fabricating one, and does not block
    /// the reading it rode in on: the abyssal side of the same reading still lands.
    /// </summary>
    [AvaloniaFact]
    public async Task AFailedLookup_LeavesTheLocationUnknown()
    {
        var monitor = new FakeMonitor();
        var names = new FakeSystemNames();   // knows no names at all
        using var instance = Build(monitor, names);
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();

        gamelog.MapCharacter(Pilot, Name);
        monitor.Report(Pilot, Jita);
        await Task.Delay(100);

        Assert.Equal(1, names.Lookups);      // it was asked — the answer is what could not be had
        Assert.Null(gamelog.Snapshot(Name).Location);
    }

    /// <summary>
    /// The point of the whole thing: a filled gap is a location the fleet-metrics screen shows and the WITH FC
    /// badge counts, because it lands in exactly the field a gamelog jump would have written.
    /// </summary>
    [AvaloniaFact]
    public async Task AFilledLocation_ReachesTheFleetSample()
    {
        var monitor = new FakeMonitor();
        using var instance = Build(monitor, new FakeSystemNames { [Jita] = "Jita" });
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();

        gamelog.MapCharacter(Pilot, Name);
        Assert.DoesNotContain(gamelog.Sample(7, Pilot, 0), s => s.Kind == MetricKind.Location);

        monitor.Report(Pilot, Jita);
        await WaitForLocationAsync(gamelog);

        var location = Assert.Single(gamelog.Sample(7, Pilot, 0), s => s.Kind == MetricKind.Location);
        Assert.Equal("Jita", location.Text);
    }

    // ---- harness ------------------------------------------------------------------------------------------------

    private static TestClientInstance Build(FakeMonitor monitor, FakeSystemNames names) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IAbyssalLocationMonitor>(monitor);
            services.AddSingleton<ISolarSystemNames>(names);
        });

    private static async Task<string?> WaitForLocationAsync(GamelogClientService gamelog)
    {
        for (var i = 0; i < 200 && gamelog.Snapshot(Name).Location is null; i++)
            await Task.Delay(10);
        return gamelog.Snapshot(Name).Location;
    }

    /// <summary>Stands in for the ESI poll loop, so a test drives readings instead of waiting on 6 s ticks.</summary>
    private sealed class FakeMonitor : IAbyssalLocationMonitor
    {
        private readonly ConcurrentDictionary<int, Action<EsiLocationReading>> _watched = new();

        public void Watch(int characterId, Action<EsiLocationReading> onReading) =>
            _watched.TryAdd(characterId, onReading);

        public void Stop(int characterId) => _watched.TryRemove(characterId, out _);

        public void UiReady() { }

        public void Report(int characterId, int solarSystemId)
        {
            if (_watched.TryGetValue(characterId, out var onReading))
                onReading(new EsiLocationReading(solarSystemId, DateTime.UtcNow));
        }

        public void Lose(int characterId)
        {
            if (_watched.TryGetValue(characterId, out var onReading))
                onReading(EsiLocationReading.Lost(DateTime.UtcNow));
        }
    }

    private sealed class FakeSystemNames : ISolarSystemNames
    {
        private readonly Dictionary<int, string> _names = new();
        private int _lookups;

        public int Lookups => Volatile.Read(ref _lookups);

        /// <summary>Blocks the answer until it completes, so a test can act while a lookup is in the air.</summary>
        public Task? Hold { get; init; }

        public string this[int solarSystemId] { set => _names[solarSystemId] = value; }

        public async Task<string?> NameAsync(int solarSystemId)
        {
            Interlocked.Increment(ref _lookups);

            if (Hold is { } hold)
                await hold;

            return _names.GetValueOrDefault(solarSystemId);
        }
    }
}
