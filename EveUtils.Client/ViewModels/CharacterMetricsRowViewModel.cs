using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Dtos;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One row in the metrics window — a single character's live session metrics. The parent drives both
/// the DPS graph (<see cref="Dps"/>, reused from the main view, fed a non-publishing sample each tick) and the
/// textual fields (from a <see cref="CharacterMetricsSnapshot"/>) on its 1 Hz refresh.
/// </summary>
public partial class CharacterMetricsRowViewModel : ViewModelBase
{
    public string Character { get; }
    public int CharacterId { get; }
    public DpsViewModel Dps { get; }

    [ObservableProperty] private string _affiliation = "—";
    [ObservableProperty] private string _location = "—";
    [ObservableProperty] private string _bounty = "0 ISK";
    [ObservableProperty] private string _kills = "0";
    [ObservableProperty] private string _iskPerHour = "—";
    [ObservableProperty] private string _hitRate = "—";
    [ObservableProperty] private string _damage = "—";
    [ObservableProperty] private string _peakDps = "—";
    [ObservableProperty] private string _enemies = "—";
    [ObservableProperty] private string _qualities = "—";
    [ObservableProperty] private string _duration = "—";
    [ObservableProperty] private string _mined = "—";
    [ObservableProperty] private string _reps = "—";
    [ObservableProperty] private string _neut = "—";
    [ObservableProperty] private bool _hasMining;
    [ObservableProperty] private bool _hasReps;
    [ObservableProperty] private bool _hasNeut;

    public ObservableCollection<string> RecentEvents { get; } = [];

    /// <summary>Latest bounty + mined value (for the window's running totals).</summary>
    public long BountyValue { get; private set; }
    public long MinedValue { get; private set; }

    private static readonly IBrush MissBrush = new SolidColorBrush(Color.Parse("#FFEF5A5A"));
    private static readonly IBrush NotifyBrush = new SolidColorBrush(Color.Parse("#FFE0B25A"));
    private int _lastMisses;
    private int _lastNotifyCount;

    public CharacterMetricsRowViewModel(string character, int characterId)
    {
        Character = character;
        CharacterId = characterId;
        Dps = new DpsViewModel(character, isSelf: true);
    }

    /// <summary>~30fps graph tick: an EMA-smoothed sample so the metrics graph scrolls + decays like the
    /// main view and the pop-out, instead of a coarse 1 Hz step squeezed into the ~20s window.</summary>
    public void TickGraph(DpsSampleDto sample) => Dps.ApplySmoothed(sample);

    /// <summary>~1 Hz refresh: drop event-markers on new misses/notifies, then update the textual fields.</summary>
    public void RefreshSnapshot(CharacterMetricsSnapshot s)
    {
        if (s.Misses > _lastMisses) Dps.AddMarker(MissBrush);
        if (s.RecentEvents.Count > _lastNotifyCount) Dps.AddMarker(NotifyBrush);
        _lastMisses = s.Misses;
        _lastNotifyCount = s.RecentEvents.Count;
        Refresh(s);
    }

    public void Refresh(CharacterMetricsSnapshot s)
    {
        BountyValue = s.BountyTotal;
        Bounty = $"{s.BountyTotal:N0} ISK";
        Kills = s.Kills.ToString();
        Location = s.Location ?? "—";
        IskPerHour = s.Duration.TotalMinutes < 1 ? "—" : $"{s.IskPerHour:N0} ISK/h";
        HitRate = s.Shots == 0 ? "—" : $"{s.HitRate * 100:0}%  ({s.Hits}/{s.Shots})";
        Damage = $"dealt {s.TotalDealt:N0} · received {s.TotalReceived:N0}";
        PeakDps = $"{s.PeakDealtDps:0} dps peak";
        Duration = $"{(int)s.Duration.TotalMinutes}m {s.Duration.Seconds:00}s";
        Enemies = s.Enemies.Count == 0 ? "—" : string.Join(", ", s.Enemies.Take(6).Select(e => $"{e.Target} ×{e.Count}"));
        Qualities = s.Qualities.Count == 0 ? "—" : string.Join(" · ", s.Qualities.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"));

        MinedValue = s.TotalMinedUnits;
        Mined = s.TotalMinedUnits == 0
            ? "—"
            : $"{s.TotalMinedUnits:N0} units · " + string.Join(", ", s.Mined.Take(4).Select(o => $"{o.OreType} {Compact(o.Units)}"));
        HasMining = s.TotalMinedUnits > 0;
        Reps = s.RepairedOut == 0 && s.RepairedIn == 0 ? "—" : $"out {s.RepairedOut:N0} · in {s.RepairedIn:N0}";
        HasReps = s.RepairedOut > 0 || s.RepairedIn > 0;
        Neut = s.NeutOut == 0 && s.NeutIn == 0 ? "—" : $"out {s.NeutOut:N0} · in {s.NeutIn:N0} GJ";
        HasNeut = s.NeutOut > 0 || s.NeutIn > 0;

        RecentEvents.Clear();
        foreach (var ev in s.RecentEvents.Take(8))
            RecentEvents.Add($"{ev.At:HH:mm:ss}  {ev.Message}");
    }

    private static string Compact(long n) => n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

    public void SetAffiliation(string label) => Affiliation = string.IsNullOrWhiteSpace(label) ? "—" : label;
}
