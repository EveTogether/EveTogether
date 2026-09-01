using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Esi;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Platform;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-63, deel 1. The gamelog only speaks when something happens: a jump or an undock writes a line, sitting still
/// writes nothing. So a pilot who was already parked where they are has no location in the app at all — right after
/// EVE Together starts, and again when a character comes online without moving. This is the one-off that closes
/// that gap from the ESI location watch ET-62 already runs, and what these cover is mostly where it stops:
/// only a gap, only outside the abyss, only once, never over a gamelog line — and only while the pilot is actually
/// in game, because ESI answers for a logged-out character too, with the spot they logged off at.
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
        using var harness = await StartAsync(monitor, new FakeSystemNames { [Jita] = "Jita" });

        Assert.Null(harness.Gamelog.Snapshot(Name).Location);   // nothing has happened in the log, so nothing is known

        monitor.Report(Pilot, Jita);

        Assert.Equal("Jita", await harness.WaitForLocationAsync());
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
        using var harness = await StartAsync(monitor, names);

        harness.Gamelog.SetLocation(Name, "Amarr", DateTime.UtcNow);   // a jump line the watcher read

        for (var i = 0; i < 5; i++)
            monitor.Report(Pilot, Jita);
        await Task.Delay(100);

        Assert.Equal("Amarr", harness.Gamelog.Snapshot(Name).Location);
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
        using var harness = await StartAsync(monitor, names);

        monitor.Report(Pilot, Jita);
        Assert.Equal("Jita", await harness.WaitForLocationAsync());

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
        using var harness = await StartAsync(monitor, new FakeSystemNames { [Jita] = "Jita" });

        monitor.Report(Pilot, Jita);
        Assert.Equal("Jita", await harness.WaitForLocationAsync());

        harness.Gamelog.SetLocation(Name, "Perimeter", DateTime.UtcNow);
        for (var i = 0; i < 5; i++)
            monitor.Report(Pilot, Jita);   // ESI still reads the cached old system for a few seconds
        await Task.Delay(100);

        Assert.Equal("Perimeter", harness.Gamelog.Snapshot(Name).Location);
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
        using var harness = await StartAsync(monitor, names);

        monitor.Report(Pilot, Jita);                            // lookup starts, and blocks
        for (var i = 0; i < 100 && names.Lookups == 0; i++)
            await Task.Delay(10);
        Assert.Equal(1, names.Lookups);                         // it really is in the air, so the race is real

        harness.Gamelog.SetLocation(Name, "Perimeter", DateTime.UtcNow); // the pilot jumps while it is in the air
        gate.SetResult();
        await Task.Delay(100);

        Assert.Equal("Perimeter", harness.Gamelog.Snapshot(Name).Location);
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
        using var harness = await StartAsync(monitor, names);

        monitor.Report(Pilot, AbyssalRoom);
        await Task.Delay(100);

        Assert.Null(harness.Gamelog.Snapshot(Name).Location);
        Assert.Equal(0, names.Lookups);

        monitor.Report(Pilot, Jita);   // they come out
        Assert.Equal("Jita", await harness.WaitForLocationAsync());
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
        using var harness = await StartAsync(monitor, names);

        monitor.Lose(Pilot);
        await Task.Delay(100);

        Assert.Null(harness.Gamelog.Snapshot(Name).Location);
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
        using var harness = await StartAsync(monitor, names);

        monitor.Report(Pilot, Jita);
        await Task.Delay(100);

        Assert.Equal(1, names.Lookups);      // it was asked — the answer is what could not be had
        Assert.Null(harness.Gamelog.Snapshot(Name).Location);
    }

    /// <summary>
    /// The point of the whole thing: a filled gap is a location the fleet-metrics screen shows and the WITH FC
    /// badge counts, because it lands in exactly the field a gamelog jump would have written.
    /// </summary>
    [AvaloniaFact]
    public async Task AFilledLocation_ReachesTheFleetSample()
    {
        var monitor = new FakeMonitor();
        using var harness = await StartAsync(monitor, new FakeSystemNames { [Jita] = "Jita" });

        Assert.DoesNotContain(harness.Gamelog.Sample(7, Pilot, 0), s => s.Kind == MetricKind.Location);

        monitor.Report(Pilot, Jita);
        await harness.WaitForLocationAsync();

        var location = Assert.Single(harness.Gamelog.Sample(7, Pilot, 0), s => s.Kind == MetricKind.Location);
        Assert.Equal("Jita", location.Text);
    }

    // ---- The operator's decision: a logged-out character's parking spot is not a location -----------------------

    /// <summary>
    /// ESI answers <c>/location/</c> for a character who is not logged in, with the system they logged off in.
    /// Recording that would put a system on screen that reads exactly like a current one and count the pilot into
    /// the WITH FC denominator as "somewhere else" — where the truth is that we do not know. So nothing is written
    /// and, since the answer could never be used, nothing is even asked.
    /// </summary>
    [AvaloniaFact]
    public async Task AnOfflineCharacter_IsNotFilledFromItsLogoffSpot()
    {
        var monitor = new FakeMonitor();
        var names = new FakeSystemNames { [Jita] = "Jita" };
        using var harness = await StartAsync(monitor, names, inGame: false);

        for (var i = 0; i < 10; i++)
            monitor.Report(Pilot, Jita);
        await Task.Delay(150);

        Assert.Null(harness.Gamelog.Snapshot(Name).Location);
        Assert.Equal(0, names.Lookups);
    }

    /// <summary>…and the moment they log in, the very next reading fills it. Offline is a pause, not a refusal.</summary>
    [AvaloniaFact]
    public async Task ComingOnline_MakesTheNextReadingFillTheGap()
    {
        var monitor = new FakeMonitor();
        var names = new FakeSystemNames { [Jita] = "Jita" };
        using var harness = await StartAsync(monitor, names, inGame: false);

        monitor.Report(Pilot, Jita);
        await Task.Delay(100);
        Assert.Null(harness.Gamelog.Snapshot(Name).Location);

        await harness.SetInGameAsync(true);
        monitor.Report(Pilot, Jita);

        Assert.Equal("Jita", await harness.WaitForLocationAsync());
    }

    // ---- ET-96: recovering a watch that died, without a registry row ever changing --------------------------------

    /// <summary>
    /// A character logging into the game client, while EVE Together already has their registry row from an earlier
    /// session, never fires <c>RegistryChanged</c> — the row does not change. Only <c>EveClientPresenceService</c>'s
    /// 5 s sweep sees it, and it has to be able to restart a watch that already died (no scope, no working token),
    /// not just the one <c>MapCharacter</c> already runs unconditionally at start-up.
    /// </summary>
    [AvaloniaFact]
    public async Task PresenceComingOnline_RestartsAWatchThatHadDied()
    {
        var monitor = new FakeMonitor();
        using var harness = await StartAsync(monitor, new FakeSystemNames { [Jita] = "Jita" }, inGame: true);
        Assert.True(monitor.IsWatching(Pilot)); // the unconditional start-up mapping already started it

        monitor.Lose(Pilot);
        Assert.False(monitor.IsWatching(Pilot));

        // PollOnce only raises Changed on a real transition, so the sweep has to actually change what it sees —
        // this is what a relog into the game client looks like from the outside.
        await harness.SetInGameAsync(false);
        Assert.False(monitor.IsWatching(Pilot)); // going offline must not itself restart anything

        await harness.SetInGameAsync(true);
        Assert.True(monitor.IsWatching(Pilot));
    }

    /// <summary>
    /// <c>ClientTokenRefreshService</c> deliberately does not touch the registry when a refresh succeeds with the
    /// same scopes (ET-24) — so a token that silently starts working again after a spell of failing needs its own
    /// path back to <c>MapCharacter</c>. <c>TokenRefreshedEvent</c> only fires on a real status change, which is
    /// exactly the transition a dead-then-recovered watch needs.
    /// </summary>
    [AvaloniaFact]
    public async Task ATokenWorkingAgain_RestartsAWatchThatHadDied()
    {
        var monitor = new FakeMonitor();
        using var harness = await StartAsync(monitor, new FakeSystemNames { [Jita] = "Jita" });
        Assert.True(monitor.IsWatching(Pilot));

        monitor.Lose(Pilot);
        Assert.False(monitor.IsWatching(Pilot));

        var bus = harness.Services.GetRequiredService<IEventBus>();
        await bus.PublishAsync(new TokenRefreshedEvent(new TokenStatusChange(Pilot, TokenStatus.Refreshed)));

        Assert.True(monitor.IsWatching(Pilot));
    }

    /// <summary>
    /// The judgement is only ever made about this client's own characters. A character the registry has never
    /// heard of is not "offline", it is none of our business — so the gate does not fire on it either way. Here
    /// that shows up as the gap staying open rather than being filled from evidence we do not have.
    /// </summary>
    [AvaloniaFact]
    public async Task ACharacterTheRegistryDoesNotKnow_IsNeverJudgedOffline()
    {
        var presence = new FakeMonitor();
        var names = new FakeSystemNames { [Jita] = "Jita" };
        using var harness = await StartAsync(presence, names, inGame: true);

        var stranger = harness.Services.GetRequiredService<ILocalCharacterPresence>();
        Assert.Null(stranger.IsInGame(90009999, "Nobody Here"));   // not ours → no claim either way
        Assert.True(stranger.IsInGame(Pilot, Name));               // ours, and in game
    }

    // ---- harness ------------------------------------------------------------------------------------------------

    private static EveClientEvidence InGame(params string[] names) =>
        new(new HashSet<string>(names, StringComparer.OrdinalIgnoreCase), new HashSet<int>());

    /// <summary>
    /// A client instance with the character registered and the running-client probe under the test's control, so
    /// "is this pilot in game" is a fact the test sets rather than whatever happens to be running on the machine.
    /// </summary>
    private static async Task<Harness> StartAsync(FakeMonitor monitor, FakeSystemNames names, bool inGame = true)
    {
        var probe = new FakeProbe { Evidence = inGame ? InGame(Name) : EveClientEvidence.Empty };
        var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IEveClientProbe>(probe);
            services.AddSingleton<IEsiLocationMonitor>(monitor);
            services.AddSingleton<ISolarSystemNames>(names);
        });

        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character(Name, Pilot));

        var harness = new Harness(instance, probe);
        await harness.SyncPresenceAsync();

        harness.Gamelog.MapCharacter(Pilot, Name);
        return harness;
    }

    private sealed class Harness(TestClientInstance instance, FakeProbe probe) : IDisposable
    {
        public IServiceProvider Services => instance.Services;

        public GamelogClientService Gamelog => Services.GetRequiredService<GamelogClientService>();

        /// <summary>Drives both sweeps the verdict rests on, so a test never races the 5 s timer or the registry load.</summary>
        public async Task SyncPresenceAsync()
        {
            Services.GetRequiredService<EveClientPresenceService>().PollOnce();
            await Services.GetRequiredService<LocalCharacterPresence>().ReloadAsync();
        }

        public async Task SetInGameAsync(bool inGame)
        {
            probe.Evidence = inGame ? InGame(Name) : EveClientEvidence.Empty;
            await SyncPresenceAsync();
        }

        public async Task<string?> WaitForLocationAsync()
        {
            for (var i = 0; i < 200 && Gamelog.Snapshot(Name).Location is null; i++)
                await Task.Delay(10);
            return Gamelog.Snapshot(Name).Location;
        }

        public void Dispose() => instance.Dispose();
    }

    /// <summary>The running-client sweep, under the test's control instead of the machine's.</summary>
    private sealed class FakeProbe : IEveClientProbe
    {
        public EveClientEvidence Evidence { get; set; } = EveClientEvidence.Empty;

        public EveClientEvidence Probe() => Evidence;
        public int RunningClientCount() => Evidence.CharacterNames.Count;
        public bool Activate(string characterName) => false;
    }

    /// <summary>Stands in for the ESI poll loop, so a test drives readings instead of waiting on 6 s ticks.</summary>
    private sealed class FakeMonitor : IEsiLocationMonitor
    {
        private readonly ConcurrentDictionary<int, Action<EsiLocationReading>> _watched = new();

        public void Watch(int characterId, string characterName, Action<EsiLocationReading> onReading) =>
            _watched.TryAdd(characterId, onReading);

        public void Stop(int characterId) => _watched.TryRemove(characterId, out _);

        public void UiReady() { }

        public bool IsWatching(int characterId) => _watched.ContainsKey(characterId);

        public void Report(int characterId, int solarSystemId)
        {
            if (_watched.TryGetValue(characterId, out var onReading))
                onReading(new EsiLocationReading(solarSystemId, DateTime.UtcNow));
        }

        public void Lose(int characterId)
        {
            // Mirrors the real monitor's Lost(): the character comes out of the running set, so a later Watch()
            // for the same id is a genuine restart rather than the idempotent no-op it would otherwise be.
            if (_watched.TryRemove(characterId, out var onReading))
                onReading(EsiLocationReading.Lost(null, DateTime.UtcNow));
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
