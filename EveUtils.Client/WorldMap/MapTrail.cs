using System;
using System.Collections.Generic;
using System.Threading;
using EveUtils.Shared.Modules.Map.Enums;

namespace EveUtils.Client.WorldMap;

/// <summary>Where a character was and when it got there.</summary>
public readonly record struct MapTrailPoint(int SolarSystemId, DateTimeOffset At);

/// <summary>
/// The systems one character has been in, oldest first. A ring buffer in memory and nothing else: it is never written
/// to a database or a file, so closing the app forgets it. Only a system change is a point — the game log's undock
/// line in the system the character is already in adds nothing.
/// </summary>
public sealed class MapTrail
{
    /// <summary>Enough for a long evening of jumping; "since app start" cannot grow without bound.</summary>
    public const int Capacity = 500;

    private static readonly TimeSpan DowntimeUtc = TimeSpan.FromHours(11);

    private readonly Lock _gate = new();
    private readonly List<MapTrailPoint> _points = [];
    private DateTimeOffset? _resetAt;

    /// <summary>When RESET TRAIL last cleared this trail, or null if it never did.</summary>
    public DateTimeOffset? ResetAt
    {
        get
        {
            lock (_gate)
                return _resetAt;
        }
    }

    public void Add(int solarSystemId, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_points.Count > 0 && _points[^1].SolarSystemId == solarSystemId)
                return;
            _points.Add(new MapTrailPoint(solarSystemId, at));
            if (_points.Count > Capacity)
                _points.RemoveAt(0);
        }
    }

    /// <summary>Starts over from the current system: everything before it is forgotten.</summary>
    public void Reset(DateTimeOffset at)
    {
        lock (_gate)
        {
            _resetAt = at;
            if (_points.Count == 0)
                return;
            MapTrailPoint current = _points[^1];
            _points.Clear();
            _points.Add(current with { At = at });
        }
    }

    /// <summary>The last <paramref name="jumps"/> jumps: that many systems plus the one they started from.</summary>
    public IReadOnlyList<MapTrailPoint> LastJumps(int jumps)
    {
        lock (_gate)
            return _points.GetRange(Math.Max(0, _points.Count - (jumps + 1)), Math.Min(_points.Count, jumps + 1));
    }

    /// <summary>
    /// The jumps made at or after <paramref name="cutoff"/>, starting from the system the first of them left. With no
    /// jump since, only the current system remains.
    /// </summary>
    public IReadOnlyList<MapTrailPoint> Since(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            int first = _points.FindIndex(point => point.At >= cutoff);
            if (first < 0)
                return _points.GetRange(Math.Max(0, _points.Count - 1), Math.Min(_points.Count, 1));
            int start = Math.Max(0, first - 1);
            return _points.GetRange(start, _points.Count - start);
        }
    }

    /// <summary>The moment a <see cref="TrailWindow.Since"/> trail starts from. Downtime is 11:00 UTC every day.</summary>
    public static DateTimeOffset CutoffFor(TrailSince since, DateTimeOffset now, DateTimeOffset appStart)
    {
        return since switch
        {
            TrailSince.Last15Minutes => now - TimeSpan.FromMinutes(15),
            TrailSince.LastHour => now - TimeSpan.FromHours(1),
            TrailSince.SinceDowntime => _LastDowntime(now),
            _ => appStart
        };
    }

    private static DateTimeOffset _LastDowntime(DateTimeOffset now)
    {
        DateTimeOffset utc = now.ToUniversalTime();
        DateTimeOffset today = new DateTimeOffset(utc.Date, TimeSpan.Zero) + DowntimeUtc;
        return today <= utc ? today : today - TimeSpan.FromDays(1);
    }
}
