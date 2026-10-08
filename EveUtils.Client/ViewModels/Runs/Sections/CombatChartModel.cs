using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Telemetry;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// What TIMELINE draws of one stored run (ET-468): the sums per bucket of every series the run had, and the seconds
/// nothing was fired either way. A rate is a bucket's sum over <see cref="BucketSeconds"/>.
/// </summary>
public sealed record CombatChartModel(
    int Seconds, int BucketSeconds, IReadOnlyDictionary<CombatSeriesKind, long[]> Buckets, bool[] Idle)
{
    /// <summary>
    /// A quiet stretch shorter than this is the gap between two volleys, not idle time.
    /// </summary>
    public const int IdleAfterSeconds = 5;

    public const int ShownBucketSeconds = 5;

    public static CombatChartModel Of(RunCombatTimelineDto timeline) => new(
        timeline.Seconds,
        ShownBucketSeconds,
        timeline.Series.ToDictionary(pair => pair.Key, pair => RunCombatTelemetry.Resample(pair.Value, ShownBucketSeconds)),
        IdleSeconds(timeline));

    public int IdleCount => Idle.Count(idle => idle);

    /// <summary>
    /// Every second inside a stretch of at least <see cref="IdleAfterSeconds"/> without damage in or out.
    /// </summary>
    public static bool[] IdleSeconds(RunCombatTimelineDto timeline)
    {
        int[] none = new int[timeline.Seconds];
        int[] dealt = timeline.Series.GetValueOrDefault(CombatSeriesKind.DmgOut, none);
        int[] taken = timeline.Series.GetValueOrDefault(CombatSeriesKind.DmgIn, none);
        bool[] idle = new bool[timeline.Seconds];
        int quietFrom = 0;
        for (int second = 0; second <= timeline.Seconds; second++)
        {
            bool quiet = second < timeline.Seconds && dealt[second] == 0 && taken[second] == 0;
            if (quiet)
            {
                continue;
            }

            if (second - quietFrom >= IdleAfterSeconds)
            {
                Array.Fill(idle, true, quietFrom, second - quietFrom);
            }
            quietFrom = second + 1;
        }

        return idle;
    }
}
