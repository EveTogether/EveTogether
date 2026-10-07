using System.IO.Compression;
using System.Runtime.InteropServices;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Telemetry;

/// <summary>
/// Turns the gamelog lines of one pilot's run into the <see cref="RunCombatTimeline"/> SAVE keeps (ET-467): sums per
/// second rather than rates, so every coarser bucket is an addition and the totals stay exact.
/// </summary>
public static class RunCombatTelemetry
{
    /// <summary>
    /// Every combat, repair, neut and capacitor line between the run's start and stop, inclusive; anything else is
    /// left out. A second without a line stays zero, which is also what a STOP/START pause leaves behind.
    /// </summary>
    public static RunCombatTimeline Build(Guid runId, IEnumerable<GameLogEvent> events, DateTime startedAtUtc,
        DateTime stoppedAtUtc)
    {
        // Gamelog times carry no kind but are EVE time, which is UTC: compared by value, never converted (ET-244).
        int seconds = (int)(stoppedAtUtc - startedAtUtc).TotalSeconds + 1;
        RunCombatTimeline timeline = new() { RunId = runId, Seconds = seconds };
        Dictionary<CombatSeriesKind, int[]> series = [];
        Dictionary<(DamageDirection, string, string?, HitQuality), RunHitTally> tallies = [];

        foreach (GameLogEvent logEvent in events.Where(logEvent =>
                     logEvent.Timestamp >= startedAtUtc && logEvent.Timestamp <= stoppedAtUtc))
        {
            int second = (int)(logEvent.Timestamp - startedAtUtc).TotalSeconds;
            switch (logEvent)
            {
                case CombatEvent hit:
                    _Add(series, hit.Direction is DamageDirection.Outgoing ? CombatSeriesKind.DmgOut : CombatSeriesKind.DmgIn,
                        seconds, second, hit.Amount);
                    _CountHit(timeline, hit);
                    _Tally(tallies, runId, hit);
                    break;
                case RemoteRepEvent rep:
                    _Add(series, rep.Outgoing ? CombatSeriesKind.RepOut : CombatSeriesKind.RepIn, seconds, second, rep.Amount);
                    break;
                case NeutEvent neut:
                    _Add(series, neut.Outgoing ? CombatSeriesKind.NeutOut : CombatSeriesKind.NeutIn, seconds, second, neut.Amount);
                    break;
                case CapTransferEvent cap:
                    _Add(series, cap.Outgoing ? CombatSeriesKind.CapOut : CombatSeriesKind.CapIn, seconds, second, cap.Amount);
                    break;
            }
        }

        // A series of misses only is all zeros: it holds nothing worth a row.
        foreach ((CombatSeriesKind kind, int[] perSecond) in series.Where(pair => pair.Value.Any(value => value != 0)))
        {
            timeline.Series.Add(new RunCombatSeries
            {
                Id = Guid.CreateVersion7(),
                RunId = runId,
                Kind = kind,
                Total = perSecond.Sum(value => (long)value),
                Samples = Encode(perSecond)
            });
        }
        foreach (RunHitTally tally in tallies.Values)
        {
            timeline.HitTallies.Add(tally);
        }

        return timeline;
    }

    /// <summary>
    /// int32 per second, deflated. Little-endian as written: every platform the app ships for is.
    /// </summary>
    public static byte[] Encode(int[] perSecond)
    {
        using MemoryStream buffer = new();
        using (DeflateStream deflate = new(buffer, CompressionLevel.SmallestSize))
        {
            deflate.Write(MemoryMarshal.AsBytes(perSecond.AsSpan()));
        }

        return buffer.ToArray();
    }

    public static int[] Decode(byte[] samples, int seconds)
    {
        int[] perSecond = new int[seconds];
        using DeflateStream deflate = new(new MemoryStream(samples), CompressionMode.Decompress);
        deflate.ReadExactly(MemoryMarshal.AsBytes(perSecond.AsSpan()));
        return perSecond;
    }

    /// <summary>
    /// The sums per bucket of <paramref name="bucketSeconds"/>; the rate of a bucket is its sum divided by its width.
    /// </summary>
    public static long[] Resample(IEnumerable<int> perSecond, int bucketSeconds) =>
        [.. perSecond.Chunk(bucketSeconds).Select(bucket => bucket.Sum(value => (long)value))];

    private static void _Add(Dictionary<CombatSeriesKind, int[]> series, CombatSeriesKind kind, int seconds, int second,
        int amount)
    {
        if (!series.TryGetValue(kind, out int[]? perSecond))
        {
            series[kind] = perSecond = new int[seconds];
        }

        // Some clients write a neut as a negative amount; the series counts how much, the kind already says which way.
        perSecond[second] += Math.Abs(amount);
    }

    private static void _CountHit(RunCombatTimeline timeline, CombatEvent hit)
    {
        bool missed = hit.Quality is HitQuality.Misses;
        if (hit.Direction is DamageDirection.Outgoing)
        {
            timeline.HitsOut += missed ? 0 : 1;
            timeline.MissesOut += missed ? 1 : 0;
            if (hit.Amount > timeline.MaxHitOut)
            {
                timeline.MaxHitOut = hit.Amount;
                timeline.MaxHitOutTarget = hit.Target;
            }
            return;
        }

        timeline.HitsIn += missed ? 0 : 1;
        timeline.MissesIn += missed ? 1 : 0;
        if (hit.Amount > timeline.MaxHitIn)
        {
            timeline.MaxHitIn = hit.Amount;
            timeline.MaxHitInSource = hit.Target;
        }
    }

    private static void _Tally(Dictionary<(DamageDirection, string, string?, HitQuality), RunHitTally> tallies, Guid runId,
        CombatEvent hit)
    {
        var key = (hit.Direction, hit.Target, hit.Weapon, hit.Quality);
        if (!tallies.TryGetValue(key, out RunHitTally? tally))
        {
            tallies[key] = tally = new RunHitTally
            {
                Id = Guid.CreateVersion7(),
                RunId = runId,
                Direction = hit.Direction,
                Counterparty = hit.Target,
                Weapon = hit.Weapon,
                Quality = hit.Quality,
                Min = hit.Amount,
                Max = hit.Amount
            };
        }

        tally.Count++;
        tally.Sum += hit.Amount;
        tally.Min = Math.Min(tally.Min, hit.Amount);
        tally.Max = Math.Max(tally.Max, hit.Amount);
    }
}
