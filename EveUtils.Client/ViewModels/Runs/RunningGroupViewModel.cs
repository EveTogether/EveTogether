using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One line in RUNNING per running group (ET-290): a homefront six of this machine's characters fly is one line with a
/// stack of their faces, not six lines with the same site and the same clock; a solo run is a group of one. Grouped on
/// the key an activity is grouped on, <c>GroupCode ?? RunId</c>.
///
/// The clock counts from the group's earliest stored start. A character who joins later has a later start of their
/// own, and the run window keeps to the commander's start for a joined site the same way — one source, so this line
/// and the window cannot drift apart. An abyssal counts down in its window and up here; that is deliberate.
/// </summary>
public sealed partial class RunningGroupViewModel(string key, Func<RunningGroupViewModel, Task> open) : ObservableObject
{
    private const int MaxFaces = 5;

    public string Key { get; } = key;

    /// <summary>This machine's characters on the group, earliest start first.</summary>
    public IReadOnlyList<RunningLaneViewModel> Lanes { get; private set; } = [];

    /// <summary>The first five faces, earliest start first — replaced as a whole when they change.</summary>
    [ObservableProperty] private IReadOnlyList<CharacterFaceViewModel> _faces = [];

    public DateTime StartedAtUtc { get; private set; }

    [ObservableProperty] private string _whoText = string.Empty;

    [ObservableProperty] private string _whoTooltip = string.Empty;

    /// <summary>"×3" beside a stack; empty for one pilot, whose name already says it.</summary>
    [ObservableProperty] private string _countText = string.Empty;

    [ObservableProperty] private string _siteText = string.Empty;

    [ObservableProperty] private string _kindText = string.Empty;

    [ObservableProperty] private string _clockText = "--:--:--";

    /// <summary>Only the first line carries the band's RUNNING label.</summary>
    [ObservableProperty] private bool _isFirst;

    public void Show(IReadOnlyList<RunningLaneViewModel> lanes, DateTime nowUtc)
    {
        RunningLaneViewModel[] byStart = [.. lanes.OrderBy(lane => lane.Run!.StartedAtUtc)];
        RunningLaneViewModel first = byStart[0];
        Lanes = byStart;
        CharacterFaceViewModel[] faces = [.. byStart.Take(MaxFaces).Select(lane => lane.Face)];
        if (!faces.SequenceEqual(Faces))
            Faces = faces;
        StartedAtUtc = first.Run!.StartedAtUtc;
        WhoText = byStart.Length == 1 ? first.CharacterText : $"{first.CharacterText} +{byStart.Length - 1}";
        WhoTooltip = string.Join(" · ", byStart.Select(lane => lane.CharacterText));
        CountText = byStart.Length > 1 ? $"×{byStart.Length}" : string.Empty;
        SiteText = first.StateText;
        KindText = first.SystemText is { } system ? $"{first.TypeText} · {system}" : first.TypeText;
        Tick(nowUtc);
    }

    public void Tick(DateTime nowUtc)
    {
        TimeSpan elapsed = nowUtc - StartedAtUtc;
        ClockText = (elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed).ToString(@"hh\:mm\:ss");
    }

    [RelayCommand]
    private Task OpenAsync() => open(this);
}
