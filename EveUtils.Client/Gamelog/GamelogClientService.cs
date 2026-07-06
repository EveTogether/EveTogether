using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Commands;
using EveUtils.Shared.Modules.Gamelog.Dtos;
using EveUtils.Shared.Modules.Gamelog.Events;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Gamelog;

/// <summary>
/// Client-side bridge between combat events and the rest of the system. Owns one DPS tracker <b>per
/// character</b> (keyed by the gamelog <c>Listener:</c> name, which every parsed line carries), persists each
/// hit (RecordCombatCommand, owner-stamped, through the gated dispatcher) and publishes a live
/// <see cref="CombatLoggedEvent"/> per character with <see cref="EventTarget.Both"/> so the local UI and —
/// once paired — the server both see the same stream. Both the real gamelog watcher and the synthetic
/// feeder drive <c>AddHitAsync</c>.
///
/// As the owner of the live DPS trackers it is also the fleet DPS <see cref="IFleetMetricSource"/>: the
/// <see cref="Fleet.FleetMetricPublisher"/> samples it ~1 Hz, addressing the <b>participating character by id</b>.
/// The id→name map (seeded from <see cref="ICharacterRegistry"/> + sign-in) is what couples a fleet sample to the
/// correct character's real combat — so a member's graph shows that member's actual DPS, not a global blob.
/// </summary>
public sealed class GamelogClientService : IFleetMetricSource, ISingletonService
{
    private readonly IServiceProvider _services;
    private readonly IEventBus _eventBus;
    private readonly ICharacterRegistry? _registry;

    // One sliding-window tracker per gamelog character name (the Listener: header — always present).
    private readonly ConcurrentDictionary<string, LiveDpsTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);

    // name <-> ESI id. The fleet is told a characterId, so we resolve it to the gamelog name to find the
    // right tracker; combat persistence + the local sample are stamped with the id when it is known.
    private readonly ConcurrentDictionary<int, string> _nameById = new();
    private readonly ConcurrentDictionary<string, int> _idByName = new(StringComparer.OrdinalIgnoreCase);

    // Per-character session metrics: combat totals, bounty, location, enemies, notable events.
    private readonly ConcurrentDictionary<string, CharacterMetrics> _metrics = new(StringComparer.OrdinalIgnoreCase);

    // Sliding-window rates for the extra live combat-graph lines (cap-warfare activity, both directions combined).
    private readonly ConcurrentDictionary<string, LiveRateTracker> _neutRate = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LiveRateTracker> _capRate = new(StringComparer.OrdinalIgnoreCase);

    // Per-RUN bounty per (fleet, character): only ISK earned while the character is participating in that fleet — the
    // fleet meter is "this run", not the persisted lifetime total. Populated by AddBountyAsync when a kill lands while
    // participating; read by the fleet sampler.
    private readonly ConcurrentDictionary<(long FleetId, int CharacterId), long> _fleetRunBounty = new();

    // One-shot persisted-state load per character: bounty + mined survive restarts.
    private readonly ConcurrentDictionary<string, Task> _seeding = new(StringComparer.OrdinalIgnoreCase);

    // How often each local tracker is sampled + published to coupled servers. The server (and its
    // dps.html / remote fleet graphs) gets a steady, decaying signal that matches the client's own 30fps graph,
    // instead of the sparse one-event-per-hit stream — the gamelog emits few discrete hits, so per-hit publishing
    // leaves long gaps the local sampler papers over but the server cannot.
    private static readonly TimeSpan RemotePublishInterval = TimeSpan.FromMilliseconds(150);

    private volatile string _localCharacter = "Capsuleer";

    /// <summary>Raised when discrete metrics (bounty/location/notify) change; the UI also polls Snapshot on a timer.</summary>
    public event Action? MetricsChanged;

    public GamelogClientService(IServiceProvider services, IEventBus eventBus, ICharacterRegistry? registry = null)
    {
        _services = services;
        _eventBus = eventBus;
        _registry = registry;

        if (_registry is not null)
        {
            _registry.RegistryChanged += () => _ = RefreshRegistryMapAsync();
            _ = RefreshRegistryMapAsync();
        }

        _ = Task.Run(() => RemotePublishLoopAsync(CancellationToken.None)); // steady remote sample stream
    }

    /// <summary>The local default character name, used before sign-in and by the synthetic feeder.</summary>
    public void SetCharacter(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            _localCharacter = name;
    }

    /// <summary>
    /// Record a name↔ESI-id pair so a fleet sample (addressed by character id) resolves to the right gamelog
    /// tracker. Called at sign-in and from the registry; idempotent.
    /// </summary>
    public void MapCharacter(int characterId, string name)
    {
        if (characterId == 0 || string.IsNullOrWhiteSpace(name))
            return;
        _nameById[characterId] = name;
        _idByName[name] = characterId;
    }

    private async Task RefreshRegistryMapAsync()
    {
        if (_registry is null)
            return;
        try
        {
            foreach (var c in await _registry.GetAllAsync())
                if (c.EsiCharacterId is { } id)
                    MapCharacter(id, c.Name);
        }
        catch
        {
            // Registry/DB not ready yet (e.g. before migration on first boot) — the next change re-runs this.
        }
    }

    /// <summary>Record a hit for the local default character (synthetic feeder / legacy callers).</summary>
    public Task AddHitAsync(DamageDirection direction, int amount, string target, CancellationToken cancellationToken = default) =>
        AddHitAsync(_localCharacter, direction, amount, target, HitQuality.Hits, occurredAt: null, cancellationToken);

    /// <summary>Record a hit attributed to a specific character — the real gamelog watcher path. Updates the
    /// in-memory tracker/metrics in real time (the 30fps sampler reads it immediately) and persists the
    /// hit; the live sample publish (server + remote relay) is offloaded so it never throttles the feed.</summary>
    public async Task AddHitAsync(string characterName, DamageDirection direction, int amount, string target, HitQuality quality = HitQuality.Hits, DateTime? occurredAt = null, CancellationToken cancellationToken = default)
    {
        var name = string.IsNullOrWhiteSpace(characterName) ? _localCharacter : characterName;

        // Place the hit at the gamelog line's OWN time, not the moment we happened to read it. EVE flushes the log
        // in chunks, so a single 500 ms poll can read several seconds of combat at once; stamping that whole batch
        // with DateTime.UtcNow piles it onto one instant and the live graph spikes then decays (a sawtooth) instead
        // of a smooth curve — and the shape then depends on each machine's disk/flush cadence, not the actual fight.
        // The sliding-window decay still samples against wall-clock "now"; only the event placement uses the log time.
        var at = occurredAt ?? DateTime.UtcNow;
        Tracker(name).Add(at, direction, amount);
        Metrics(name).RecordCombat(direction, amount, target, quality);

        var ownerId = _idByName.TryGetValue(name, out var id) ? id : (int?)null;
        using (var scope = _services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            await dispatcher.Send(new RecordCombatCommand(ownerId, amount, direction, target, at), cancellationToken);
        }

        // Local delivery is synchronous (drives the bus + UI immediately). The remote leg is NOT sent per hit —
        // the steady RemotePublishLoopAsync sampler streams it instead, so the server sees the same continuous,
        // decaying curve the local 30fps graph does rather than a few sparse per-hit points.
        await PublishSampleAsync(name, EventTarget.Local, cancellationToken);
    }

    // Steady remote sampler: every RemotePublishInterval, sample each active local tracker against "now"
    // and publish it to the coupled servers. An idle tracker (decayed to zero) is skipped, so this is quiet
    // between fights; while fighting — and during the ~5s decay tail after the last hit — it streams a smooth,
    // server-faithful curve. Self-throttling: a slow per-server gRPC write only delays the next sample, it never
    // touches the in-memory tracker the local graph reads, and nothing queues unboundedly.
    private async Task RemotePublishLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                foreach (var name in _trackers.Keys)
                {
                    var sample = Tracker(name).Sample(DateTime.UtcNow);
                    if (sample.Dealt <= 0 && sample.Received <= 0)
                        continue;

                    Metrics(name).ObservePeakDps(sample.Dealt);
                    var id = _idByName.TryGetValue(name, out var cid) ? cid : (int?)null;
                    var dto = new DpsSampleDto(id, name, (long)sample.Dealt, (long)sample.Received, DateTimeOffset.UtcNow);
                    await _eventBus.PublishAsync(new CombatLoggedEvent(dto, id), EventTarget.Remote, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                try
                {
                    _services.GetService<ILoggerFactory>()?.CreateLogger<GamelogClientService>()
                        .LogError(ex, "Remote DPS sample publish failed");
                }
                catch { /* logging must never throw from the publish loop */ }
            }

            try { await Task.Delay(RemotePublishInterval, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Samples + publishes the local default character — kept for timer-driven decay callers.</summary>
    public Task PublishSampleAsync(CancellationToken cancellationToken = default) =>
        PublishSampleAsync(_localCharacter, cancellationToken);

    /// <summary>Samples one character's tracker against "now" and publishes it (decaying graph).</summary>
    public Task PublishSampleAsync(string characterName, CancellationToken cancellationToken = default)
        => PublishSampleAsync(characterName, EventTarget.Both, cancellationToken);

    /// <summary>Sample + publish to a specific target. <c>AddHitAsync</c> publishes the local leg
    /// synchronously (bus/UI) and offloads the remote leg, so the slow per-server send never throttles the feed.</summary>
    public Task PublishSampleAsync(string characterName, EventTarget target, CancellationToken cancellationToken = default)
    {
        var name = string.IsNullOrWhiteSpace(characterName) ? _localCharacter : characterName;
        var sample = Tracker(name).Sample(DateTime.UtcNow);
        Metrics(name).ObservePeakDps(sample.Dealt);
        var id = _idByName.TryGetValue(name, out var cid) ? cid : (int?)null;
        var dto = new DpsSampleDto(id, name, (long)sample.Dealt, (long)sample.Received, DateTimeOffset.UtcNow);
        return _eventBus.PublishAsync(new CombatLoggedEvent(dto, id), target, cancellationToken);
    }

    /// <summary>True if this character has a local gamelog DPS tracker (its DPS is sampled here, not relayed from
    /// a remote fleet member). The UI render timer uses this to drive only the local graphs smoothly.</summary>
    public bool HasLocalTracker(string name) => _trackers.ContainsKey(Resolve(name));

    /// <summary>Sample a local character's current (decaying) DPS <b>without publishing</b> — for the ~30fps UI
    /// render timer that scrolls the local graph between hits. Zero DPS if no tracker exists yet.</summary>
    public DpsSampleDto SampleDps(string name)
    {
        var resolved = Resolve(name);
        var sample = _trackers.TryGetValue(resolved, out var tracker) ? tracker.Sample(DateTime.UtcNow) : new DpsSample(0, 0);
        var id = _idByName.TryGetValue(resolved, out var cid) ? cid : (int?)null;
        return new DpsSampleDto(id, resolved, (long)sample.Dealt, (long)sample.Received, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The fleet DPS samples for this tick: the damage dealt (<see cref="MetricKind.Dps"/>) and
    /// received (<see cref="MetricKind.DpsIn"/>) per second of the <b>participating</b> character, resolved from
    /// its id to its gamelog tracker. Both are emitted every tick — including zero (no tracker / not fighting) —
    /// so a member's live graph (both series) decays back to zero when combat stops, and both series stay in
    /// lockstep on the receiver. Only the participating, id-known character is shared, so the value is coupled to
    /// the right pilot.
    /// </summary>
    public IEnumerable<MetricSample> Sample(long fleetId, int characterId, long unixMs)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
        var dps = new DpsSample(0, 0);
        double neut = 0, cap = 0;
        string? system = null;
        if (_nameById.TryGetValue(characterId, out var name))
        {
            if (_trackers.TryGetValue(name, out var tracker))
                dps = tracker.Sample(now);
            if (_neutRate.TryGetValue(name, out var neutRate))
                neut = neutRate.Sample(now);
            if (_capRate.TryGetValue(name, out var capRate))
                cap = capRate.Sample(now);
            if (_metrics.TryGetValue(name, out var metrics))
                system = metrics.Location; // last known solar system from the gamelog jump/undock
        }

        // Bounty is per fleet RUN: only ISK earned since this character started participating in this fleet (the
        // persisted lifetime total stays out of the fleet meter).
        var bounty = (double)_fleetRunBounty.GetValueOrDefault((fleetId, characterId));

        // Every combat rate every tick — including zero — so each live graph line decays back to zero when it stops.
        yield return new MetricSample(characterId, fleetId, MetricKind.Dps, dps.Dealt, unixMs);
        yield return new MetricSample(characterId, fleetId, MetricKind.DpsIn, dps.Received, unixMs);
        yield return new MetricSample(characterId, fleetId, MetricKind.Neut, neut, unixMs);
        yield return new MetricSample(characterId, fleetId, MetricKind.Cap, cap, unixMs);
        // Bounty is a cumulative ISK total (not a rate): the receiver shows the latest + the fleet sums them.
        yield return new MetricSample(characterId, fleetId, MetricKind.Bounty, bounty, unixMs);

        // The participating character's current system as a State sample — the share-gate (Location opt-in) decides
        // whether it leaves this client. Only emitted once a position is actually known (no fabricated "—").
        if (!string.IsNullOrEmpty(system))
            yield return new MetricSample(characterId, fleetId, MetricKind.Location, 0, unixMs, system);
    }

    /// <summary>The local character's full set of live combat rates (DPS out/in + neut + cap GJ/s) without publishing —
    /// for the shared 30fps render driver to scroll every line of an own meter smoothly between gamelog ticks.</summary>
    public CombatRates SampleCombat(string name)
    {
        var resolved = Resolve(name);
        var now = DateTime.UtcNow;
        var dps = _trackers.TryGetValue(resolved, out var tracker) ? tracker.Sample(now) : new DpsSample(0, 0);
        var neut = _neutRate.TryGetValue(resolved, out var neutRate) ? neutRate.Sample(now) : 0;
        var cap = _capRate.TryGetValue(resolved, out var capRate) ? capRate.Sample(now) : 0;
        return new CombatRates(dps.Dealt, dps.Received, neut, cap);
    }

    /// <summary>Record a bounty payout (one kill); persisted across restarts. If the character is
    /// participating in a fleet right now, the payout is also added to that fleet's per-run bounty (fleet meter).</summary>
    public async Task AddBountyAsync(string characterName, long isk)
    {
        var name = Resolve(characterName);
        await EnsureSeededAsync(name);
        Metrics(name).RecordBounty(isk);
        AddFleetRunBounty(name, isk);
        MetricsChanged?.Invoke();
        await PersistAsync(name);
    }

    // Attribute a kill's bounty to every fleet this character is participating in right now, so the fleet meter counts
    // only ISK earned during the run — a kill landed before joining (not participating) is never added.
    private void AddFleetRunBounty(string name, long isk)
    {
        if (!_idByName.TryGetValue(name, out var characterId))
            return;
        var participation = _services.GetService<IFleetParticipation>();
        if (participation is null)
            return;
        foreach (var participant in participation.Current)
            if (participant.CharacterId == characterId)
                _fleetRunBounty.AddOrUpdate((participant.FleetId, characterId), isk, (_, previous) => previous + isk);
    }

    /// <summary>Record a mining cycle; mined units per ore persisted across restarts.</summary>
    public async Task AddMiningAsync(string characterName, MiningEvent mining)
    {
        var name = Resolve(characterName);
        await EnsureSeededAsync(name);
        Metrics(name).RecordMining(mining);
        MetricsChanged?.Invoke();
        await PersistAsync(name);
    }

    /// <summary>Record a remote rep (logi → you / you → fleetmate); session-only.</summary>
    public void AddRemoteRep(string characterName, bool outgoing, int amount)
    {
        Metrics(Resolve(characterName)).RecordRemoteRep(outgoing, amount);
        MetricsChanged?.Invoke();
    }

    /// <summary>Record an energy-neutralizer hit (cap warfare); session-only. Feeds the directional cumulative
    /// (per-character readout) and the combined sliding-window rate (the live Neut graph line).</summary>
    public void AddNeut(string characterName, bool outgoing, int amount, DateTime? occurredAt = null)
    {
        var name = Resolve(characterName);
        Metrics(name).RecordNeut(outgoing, amount);
        NeutRate(name).Add(occurredAt ?? DateTime.UtcNow, amount);   // log-line time, not read time (smooth, not spiky)
        MetricsChanged?.Invoke();
    }

    /// <summary>Record a remote-capacitor transfer (cap support); session-only. Feeds the combined
    /// sliding-window rate (the live Cap graph line).</summary>
    public void AddCapTransfer(string characterName, bool outgoing, int amount, DateTime? occurredAt = null)
    {
        CapRate(Resolve(characterName)).Add(occurredAt ?? DateTime.UtcNow, amount);   // log-line time, not read time
        MetricsChanged?.Invoke();
    }

    /// <summary>Load the persisted bounty + mined for a character into the accumulator, exactly once.</summary>
    public Task EnsureSeededAsync(string characterName)
    {
        var name = Resolve(characterName);
        return _seeding.GetOrAdd(name, SeedAsync);
    }

    private async Task SeedAsync(string name)
    {
        try
        {
            using var scope = _services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ICharacterMetricStateRepository>();
            var state = await store.GetAsync(name);
            if (state is not null)
                Metrics(name).SeedPersisted(state.BountyTotal, state.Kills, DeserializeMined(state.MinedJson));
        }
        catch
        {
            // DB not ready / no row — start fresh; a later bounty/mining still persists.
        }
    }

    private async Task PersistAsync(string name)
    {
        var snapshot = Metrics(name).Snapshot(name);
        var minedJson = JsonSerializer.Serialize(snapshot.Mined.ToDictionary(o => o.OreType, o => o.Units));
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICharacterMetricStateRepository>();
        await store.UpsertAsync(name, snapshot.BountyTotal, snapshot.Kills, minedJson);
    }

    private static IReadOnlyList<OreTotal> DeserializeMined(string json)
    {
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? [];
            return map.Select(kv => new OreTotal(kv.Key, kv.Value, 0, 0)).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Update a character's current solar system from a gamelog jump/undock.</summary>
    public void SetLocation(string characterName, string system)
    {
        Metrics(Resolve(characterName)).SetLocation(system);
        MetricsChanged?.Invoke();
    }

    /// <summary>Record a notable notify/warning event (scramble, jam, neut, …).</summary>
    public void AddNotify(string characterName, DateTime at, string message)
    {
        Metrics(Resolve(characterName)).RecordNotify(at, message);
        MetricsChanged?.Invoke();
    }

    /// <summary>An immutable snapshot of a character's session metrics for the metrics view.</summary>
    public CharacterMetricsSnapshot Snapshot(string characterName)
    {
        var name = Resolve(characterName);
        _ = EnsureSeededAsync(name); // self-seed persisted bounty/mined on first view (idempotent)
        return Metrics(name).Snapshot(name);
    }

    /// <summary>A non-publishing DPS sample for a character — for UI polling / graph decay (no bus, no mutation).</summary>
    public DpsSampleDto PeekSample(string characterName)
    {
        var name = Resolve(characterName);
        var sample = Tracker(name).Sample(DateTime.UtcNow);
        var id = _idByName.TryGetValue(name, out var cid) ? cid : (int?)null;
        return new DpsSampleDto(id, name, (long)sample.Dealt, (long)sample.Received, DateTimeOffset.UtcNow);
    }

    private string Resolve(string name) => string.IsNullOrWhiteSpace(name) ? _localCharacter : name;
    private CharacterMetrics Metrics(string name) => _metrics.GetOrAdd(name, _ => new CharacterMetrics());
    private LiveDpsTracker Tracker(string name) => _trackers.GetOrAdd(name, _ => new LiveDpsTracker());
    private LiveRateTracker NeutRate(string name) => _neutRate.GetOrAdd(name, _ => new LiveRateTracker());
    private LiveRateTracker CapRate(string name) => _capRate.GetOrAdd(name, _ => new LiveRateTracker());
}
