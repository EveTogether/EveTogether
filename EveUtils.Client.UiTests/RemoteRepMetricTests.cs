using System;
using System.Linq;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Reps received travel the fleet bus the same way <see cref="MetricKind.NeutIn"/> does: recorded on the log
/// line's own time into a sliding-window rate, then sampled per tick as their own <see cref="MetricKind"/> — never
/// as a second meaning of an existing one. A gap in any one of these steps fails silently (nothing throws; the
/// figure is just never shown), which is what these tests exist to catch.
/// </summary>
public class RemoteRepMetricTests
{
    private const int CharacterId = 90000123;
    private const long FleetId = 7;

    [AvaloniaFact]
    public void IncomingRep_IsSampledAsRepIn_AtTheLogTimestamp_NotProcessingTime()
    {
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(CharacterId, "Pilot");

        var repAt = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        gamelog.AddRemoteRep("Pilot", outgoing: false, amount: 500, occurredAt: repAt);

        static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
        double RepInAt(DateTime now) =>
            gamelog.Sample(FleetId, CharacterId, Ms(now)).First(s => s.Kind == MetricKind.RepIn).Value;

        // 4 s after the rep's own time → a lone cycle, its interval not known yet, is held over the base 5 s → 100 hp/s.
        Assert.Equal(100, RepInAt(repAt.AddSeconds(4)));
        // Long after → faded out → 0. (Stamped at read time instead, the rep would sit at the test's real wall clock,
        // far from 2030, and even the first reading would be 0.)
        Assert.Equal(0, RepInAt(repAt.AddSeconds(20)));
    }

    [AvaloniaFact]
    public void OutgoingRep_DoesNotFeedRepIn()
    {
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(CharacterId, "Pilot");

        var repAt = DateTime.UtcNow;
        gamelog.AddRemoteRep("Pilot", outgoing: true, amount: 500, occurredAt: repAt);

        var samples = gamelog.Sample(FleetId, CharacterId, new DateTimeOffset(repAt.AddSeconds(1)).ToUnixTimeMilliseconds()).ToList();

        Assert.Equal(0, samples.First(s => s.Kind == MetricKind.RepIn).Value);
        // It is the logi's own output, and since ET-277 it has a kind of its own.
        Assert.Equal(100, samples.First(s => s.Kind == MetricKind.RepOut).Value);
    }

    [Fact]
    public void RepIn_SharesTheCombatGate_WithDps()
    {
        Assert.True(MetricShareSnapshot.IsCombat(MetricKind.RepIn));
        Assert.Equal(MetricShareSnapshot.KeyFor(MetricKind.Dps), MetricShareSnapshot.KeyFor(MetricKind.RepIn));
        Assert.Equal(MetricShareSnapshot.OverrideKeyFor(7, 42, MetricKind.Dps),
                     MetricShareSnapshot.OverrideKeyFor(7, 42, MetricKind.RepIn));
    }
}
