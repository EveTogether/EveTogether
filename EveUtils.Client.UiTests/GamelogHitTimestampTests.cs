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
}
