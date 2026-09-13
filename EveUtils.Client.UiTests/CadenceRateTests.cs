using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-277: the live rates adapt to each stream's own cadence. A fixed 5 s window read a 10 s missile volley as a spike
/// followed by nothing, so the DPS line was a sawtooth and the number never showed the real DPS. Every scenario stamps
/// its lines the way the game log does — whole seconds — and replays them as they would be read: a sample only ever
/// sees the lines written before it. A plain moving average over 10 s is the yardstick: the adaptive rate has to be
/// at least as flat on slow weapons and at least as quick on fast ones.
/// </summary>
public sealed class CadenceRateTests
{
    private static readonly DateTime FightStart = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Launcher = "Nova Rage Heavy Assault Missile";
    private const string Lasers = "Mega Pulse Laser II";

    private sealed record Line(DateTime At, int Amount, string Stream, DamageDirection Direction = DamageDirection.Outgoing);

    // Four launchers cycling every ~10.3 s, each missile landing 3.5–5 s after it was fired (the range drifts), a
    // quarter-second spread between launchers: the shape of a HAM boat's lines.
    private static List<Line> MissileVolleys(int volleys, out double trueDps)
    {
        var random = new Random(277);
        var lines = new List<Line>();
        for (var volley = 0; volley < volleys; volley++)
        {
            var fired = volley * 10.3 + random.NextDouble() * 0.3;
            var flight = 3.5 + 1.5 * (0.5 + 0.5 * Math.Sin(volley / 4.0));
            for (var launcher = 0; launcher < 4; launcher++)
                lines.Add(new Line(LogTime(fired + flight + launcher * 0.25), 1192, Launcher));
        }
        trueDps = 4 * 1192 / 10.3;
        return lines.OrderBy(line => line.At).ToList();
    }

    private static List<Line> Cycle(double from, double to, double cycle, Func<int> amount, string stream,
        DamageDirection direction = DamageDirection.Outgoing)
    {
        var lines = new List<Line>();
        for (var t = from; t < to; t += cycle)
            lines.Add(new Line(LogTime(t), amount(), stream, direction));
        return lines;
    }

    private static DateTime LogTime(double seconds) => FightStart.AddSeconds(Math.Floor(seconds));

    private static DateTime At(double seconds) => FightStart.AddSeconds(seconds);

    /// <summary>Samples the tracker every quarter second from <paramref name="from"/> to <paramref name="to"/>, feeding
    /// it each line once the log has written it — as the watcher does — before the sample that could see it.</summary>
    private static List<(DateTime Now, DpsSample Rate)> Replay(LiveDpsTracker tracker, IEnumerable<Line> lines,
        DateTime from, DateTime to)
    {
        var pending = new Queue<Line>(lines.OrderBy(line => line.At));
        var samples = new List<(DateTime, DpsSample)>();
        for (var now = FightStart; now <= to; now = now.AddSeconds(0.25))
        {
            while (pending.Count > 0 && pending.Peek().At <= now)
            {
                var line = pending.Dequeue();
                tracker.Add(line.At, line.Direction, line.Amount, line.Stream);
            }
            var rate = tracker.Sample(now);
            if (now >= from)
                samples.Add((now, rate));
        }
        return samples;
    }

    // The yardstick: everything in the last ten seconds, divided by ten.
    private static double TenSecondWindow(IEnumerable<Line> lines, DateTime now) =>
        lines.Where(line => line.At <= now && line.At > now.AddSeconds(-10)).Sum(line => line.Amount) / 10.0;

    private static double Spread(IReadOnlyCollection<double> values, double reference) =>
        (values.Max() - values.Min()) / reference;

    [Fact]
    public void MissileVolleys_EveryTenSeconds_WithFlightTime_ReadAsAFlatLine()
    {
        var lines = MissileVolleys(30, out var trueDps);

        // From the third volley on — the cadence is known by then — until the last volley lands.
        var from = lines.Select(line => line.At).Distinct().ElementAt(6);
        var samples = Replay(new LiveDpsTracker(), lines, from, lines[^1].At);
        var dealt = samples.Select(sample => sample.Rate.Dealt).ToList();
        var yardstick = samples.Select(sample => TenSecondWindow(lines, sample.Now)).ToList();

        Assert.All(dealt, dps => Assert.InRange(dps, trueDps * 0.9, trueDps * 1.1));
        Assert.True(Spread(dealt, trueDps) <= Spread(yardstick, trueDps),
            $"adaptive spread {Spread(dealt, trueDps):P0} is wider than a plain 10 s window's {Spread(yardstick, trueDps):P0}");
    }

    [Fact]
    public void MissileVolleys_WhenTheShootingStops_HoldOneIntervalThenFadeToZero()
    {
        var lines = MissileVolleys(12, out var trueDps);
        var last = lines[^1].At;
        var samples = Replay(new LiveDpsTracker(), lines, last, last.AddSeconds(45))
            .ToDictionary(sample => sample.Now, sample => sample.Rate.Dealt);

        // One missed volley is not the end of a fight: the next may simply still be on its way.
        Assert.True(samples[last.AddSeconds(9)] >= trueDps * 0.9, $"{samples[last.AddSeconds(9)]:0} after one interval");
        // A few missed volleys are.
        Assert.True(samples[last.AddSeconds(20)] < trueDps * 0.75, $"{samples[last.AddSeconds(20)]:0} after two intervals");
        Assert.Equal(0, samples[last.AddSeconds(40)]);
    }

    [Fact]
    public void FastTurrets_FollowAChangeAtLeastAsQuicklyAsATenSecondWindow_AndStopAsQuickly()
    {
        // A laser group every 2.5 s, then a switch to a harder-hitting crystal at 60 s, then silence from 120 s.
        var lines = Cycle(0, 60, 2.5, () => 1100, Lasers).Concat(Cycle(60, 120, 2.5, () => 2200, Lasers)).ToList();
        var samples = Replay(new LiveDpsTracker(), lines, At(10), At(140));
        const double before = 1100 / 2.5, after = 2200 / 2.5;

        // Steady fire reads flat, although a 2.5 s cycle logs as alternating gaps of 2 and 3 whole seconds.
        var steady = samples.Where(sample => sample.Now < At(60)).ToList();
        Assert.All(steady, sample => Assert.InRange(sample.Rate.Dealt, before * 0.9, before * 1.1));
        var adaptiveSpread = Spread(steady.Select(sample => sample.Rate.Dealt).ToList(), before);
        var yardstickSpread = Spread(steady.Select(sample => TenSecondWindow(lines, sample.Now)).ToList(), before);
        Assert.True(adaptiveSpread <= yardstickSpread + 0.01, $"adaptive spread {adaptiveSpread:P1}, a 10 s window {yardstickSpread:P1}");

        var adaptive = samples.First(sample => sample.Now >= At(60) && sample.Rate.Dealt >= after * 0.95).Now;
        var yardstick = samples.First(sample => sample.Now >= At(60) && TenSecondWindow(lines, sample.Now) >= after * 0.95).Now;
        Assert.True(adaptive <= yardstick, $"settled {(adaptive - At(60)).TotalSeconds} s after the change, a 10 s window {(yardstick - At(60)).TotalSeconds} s");
        Assert.True(adaptive <= At(67), $"took {(adaptive - At(60)).TotalSeconds} s to follow the new crystal");

        var last = lines[^1].At;
        var zero = samples.First(sample => sample.Now > last && sample.Rate.Dealt == 0).Now;
        Assert.True(zero <= last.AddSeconds(10), $"still reading DPS {(zero - last).TotalSeconds} s after the last shot");
    }

    [Fact]
    public void ALongMixedFight_AveragesToTheTotalDamageOverTheTime()
    {
        var random = new Random(9);
        var lines = Cycle(0, 300, 2.5, () => 900 + random.Next(0, 600), Lasers)
            .Concat(Enumerable.Range(0, 5).SelectMany(drone => Cycle(drone * 0.7, 300, 4.1, () => 60 + random.Next(0, 80), "Acolyte II")))
            .Concat(MissileVolleys(29, out _))
            .ToList();

        var samples = Replay(new LiveDpsTracker(), lines, FightStart, At(300));
        var total = lines.Sum(line => line.Amount);

        Assert.InRange(samples.Average(sample => sample.Rate.Dealt), total / 300.0 * 0.97, total / 300.0 * 1.03);
    }

    [Fact]
    public void TwoWeapons_OnDifferentCycles_EachKeepTheirOwnCadence()
    {
        // Drones landing every couple of seconds must not shrink the missiles' hold down to the drones' interval.
        var lines = MissileVolleys(20, out var missileDps).Concat(Cycle(0, 210, 2, () => 150, "Hobgoblin II")).ToList();
        var expected = missileDps + 150 / 2.0;

        Assert.All(Replay(new LiveDpsTracker(), lines, At(40), At(200)),
            sample => Assert.InRange(sample.Rate.Dealt, expected * 0.9, expected * 1.1));
    }

    [Fact]
    public void TheFirstHitOfAnEngagement_ShowsAtOnce_InsteadOfRampingIn()
    {
        var tracker = new LiveDpsTracker();
        tracker.Add(FightStart, DamageDirection.Outgoing, 500, Lasers);

        Assert.Equal(100, tracker.Sample(At(0.5)).Dealt);
        Assert.Equal(0, tracker.Sample(At(60)).Dealt);
    }

    [Fact]
    public void IncomingAndOutgoing_AreMeasuredApart()
    {
        var lines = Cycle(0, 30, 2, () => 400, Lasers)
            .Concat(Cycle(0, 30, 2, () => 100, "Shadow's Wingman", DamageDirection.Incoming))
            .ToList();

        var (_, rate) = Replay(new LiveDpsTracker(), lines, At(20), At(20)).Single();
        Assert.InRange(rate.Dealt, 180, 220);
        Assert.InRange(rate.Received, 45, 55);
    }

    [Fact]
    public void ACapTransmitterCycle_ReadsAsItsSteadyRate_InsteadOfBlinking()
    {
        // A Corpum C-Type Medium Remote Capacitor Transmitter: 366 GJ every ~14.6 s, about 25 GJ/s.
        var tracker = new LiveRateTracker();
        var cycles = new Queue<DateTime>(Enumerable.Range(0, 13).Select(cycle => LogTime(cycle * 14.6)));
        for (var now = FightStart; now <= At(175); now = now.AddSeconds(0.5))
        {
            while (cycles.Count > 0 && cycles.Peek() <= now)
                tracker.Add(cycles.Dequeue(), 366, "HotSprockets - Corpum C-Type Medium Remote Capacitor Transmitter");
            if (now >= At(45))
                Assert.InRange(tracker.Sample(now), 25 * 0.88, 25 * 1.12);
        }
    }
}
