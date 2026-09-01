using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// A live combat hit must be placed on the DPS sliding window at the gamelog line's OWN time, not at the moment the
/// watcher reads it. EVE flushes the log in chunks, so one poll can ingest several seconds of combat at once; stamping
/// that batch with the read time piles it onto one instant and the graph spikes/decays (a sawtooth) instead of a
/// smooth curve — and the shape would then depend on each machine's disk/flush cadence rather than the actual fight.
/// </summary>
public class GamelogHitTimestampTests
{
    [AvaloniaFact]
    public async Task CombatHit_IsPlacedAtTheLogTimestamp_NotProcessingTime()
    {
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        const int characterId = 90000123;
        const long fleetId = 7;
        gamelog.MapCharacter(characterId, "Pilot");

        // An in-game hit whose log time is far from the test's wall clock, as if read from a flushed backlog.
        var hitTime = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await gamelog.AddHitAsync("Pilot", DamageDirection.Outgoing, 500, "Rat", HitQuality.Hits, hitTime);

        static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
        double DpsAt(DateTime now) =>
            gamelog.Sample(fleetId, characterId, Ms(now)).First(s => s.Kind == MetricKind.Dps).Value;

        // 4 s after the hit's own time → still inside the 5 s window → 500 / 5 = 100 dps.
        Assert.Equal(100, DpsAt(hitTime.AddSeconds(4)));
        // 6 s after → aged out of the window → 0. (With the old DateTime.UtcNow stamping, the hit would sit at the
        // test's real wall clock, far from 2030, so even DpsAt(2030+4 s) would already read 0 — red without the fix.)
        Assert.Equal(0, DpsAt(hitTime.AddSeconds(6)));
    }

    /// <summary>
    /// The same rule for the enemy-observation feed (ET-105's storage seam): subscribers are handed the log line's
    /// own time, so what ends up in a saved run's observations is what was witnessed. Without it the run would
    /// carry first/last times that look like a measurement but are only the moment the file was polled — and a
    /// later room-boundary analysis (ET-55) reads exactly that gap.
    /// </summary>
    [AvaloniaFact]
    public async Task CombatObservation_CarriesTheLogTimestamp_NotProcessingTime()
    {
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        const int characterId = 90000123;
        gamelog.MapCharacter(characterId, "Pilot");

        DateTime? observedAt = null;
        gamelog.CombatObserved += (_, _, at, _) => observedAt = at;

        var hitTime = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await gamelog.AddHitAsync("Pilot", DamageDirection.Outgoing, 500, "Centii Servant", HitQuality.Hits, hitTime);

        Assert.Equal(hitTime, observedAt);
    }

    /// <summary>
    /// The feed fires for <b>both</b> directions — the gamelog carries <c>250 to Centii Scavenger</c> and
    /// <c>1 from Centii Servant</c> alike — and hands each subscriber the real one. Without this a rat that only
    /// ever shot at you would be stored as one you shot at, which is a fact nobody measured.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(DamageDirection.Outgoing)]
    [InlineData(DamageDirection.Incoming)]
    public async Task CombatObservation_CarriesTheRealDamageDirection(DamageDirection direction)
    {
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000123, "Pilot");

        DamageDirection? observed = null;
        gamelog.CombatObserved += (_, _, _, d) => observed = d;

        await gamelog.AddHitAsync("Pilot", direction, 500, "Centii Servant", HitQuality.Hits,
            new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(direction, observed);
    }
}
