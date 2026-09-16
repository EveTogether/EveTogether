using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One local character's place in RUNNING: the run it is on, or nothing. The band draws a line per running group from
/// these (<see cref="RunningGroupViewModel"/>, ET-290) and an avatar for every character with nothing running — a
/// pilot sitting still keeps a face in the band, because a toon that disappears from it is a toon you forget
/// (ET-161 AC-6).
///
/// The lane sends the pilot to the screen that owns the action rather than carrying a second copy of it. An idle
/// avatar opens the manual run-start screen fixed on this character (the one-click start of ET-200): type and site are
/// chosen there, since a run started on a remembered guess gets the wrong name. A running line opens the run window,
/// which is where STOP lives. A STOP here would be a second idea of what stopping is — putting the clock to rest without
/// ending the fleet announcement, the enemy observations or the loot refresh the run window does — and that split is
/// the exact bug <c>SetRunStoppedCommand</c> was written to close.
/// </summary>
public sealed partial class RunningLaneViewModel(Character character, CharacterFaceViewModel face,
    Func<RunningLaneViewModel, Task> act) : ViewModelBase
{
    public Character Character { get; } = character;

    public string CharacterText { get; } = character.Name;

    public CharacterFaceViewModel Face { get; } = face;

    public string StartTooltip => $"{CharacterText} · start a run";

    /// <summary>The run on this lane, or null when the pilot is sitting still.</summary>
    public RunningRunDto? Run { get; private set; }

    [ObservableProperty] private bool _isRunning;

    /// <summary>What the pilot is on, or that they are on nothing. Never blank: an empty lane that says nothing
    /// looks like a lane that failed to load.</summary>
    [ObservableProperty] private string _stateText = "nothing running";

    /// <summary>Time on the clock, counted from the stored start — the same anchor the run window counts from, so
    /// the two cannot drift apart.</summary>
    [ObservableProperty] private string _clockText = "--:--:--";

    [ObservableProperty] private string _actionText = "START";

    /// <summary>TYPE of the run, from the catalogue every run list reads (ET-226).</summary>
    [ObservableProperty] private string _typeText = string.Empty;

    /// <summary>The run's solar system, when it recorded one the static data knows.</summary>
    [ObservableProperty] private string? _systemText;

    public void Attach(RunningRunDto? run, DateTime nowUtc, string typeText = "", string? systemText = null)
    {
        Run = run;
        IsRunning = run is not null;
        ActionText = run is null ? "START" : "OPEN";
        StateText = run is null
            ? "nothing running"
            : string.IsNullOrWhiteSpace(run.SiteName) ? "unnamed site" : run.SiteName;
        TypeText = run is null ? string.Empty : typeText;
        SystemText = run is null ? null : systemText;
        Tick(nowUtc);
    }

    public void Tick(DateTime nowUtc)
    {
        if (Run is not { } run)
        {
            ClockText = "--:--:--";
            return;
        }

        TimeSpan elapsed = nowUtc - run.StartedAtUtc;
        ClockText = (elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed).ToString(@"hh\:mm\:ss");
    }

    [RelayCommand]
    private Task ActAsync() => act(this);
}
