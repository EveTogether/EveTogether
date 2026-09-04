using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One lane per local character, whether or not anything is running on it. A pilot who is sitting still keeps their
/// lane and gets a START, because a toon that disappears from the band is a toon you forget (ET-161 AC-6) — the
/// band is a roster, and filtering it down to "has a running run" is what that criterion catches.
///
/// The lane sends the pilot to the screen that owns the action rather than carrying a second copy of it: START goes
/// to the manual run-start screen with this character already chosen (ET-163), and a running lane opens the run
/// window, which is where STOP lives. A STOP here would be a second idea of what stopping is — putting the clock to
/// rest without ending the fleet announcement, the enemy observations or the loot refresh the run window does — and
/// that split is the exact bug <c>SetRunStoppedCommand</c> was written to close.
/// </summary>
public sealed partial class RunningLaneViewModel(Character character, Func<RunningLaneViewModel, Task> act)
    : ViewModelBase
{
    public Character Character { get; } = character;

    public string CharacterText { get; } = character.Name;

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

    public void Attach(RunningRunDto? run, DateTime nowUtc)
    {
        Run = run;
        IsRunning = run is not null;
        ActionText = run is null ? "START" : "OPEN";
        StateText = run is null
            ? "nothing running"
            : string.IsNullOrWhiteSpace(run.SiteName) ? "unnamed site" : run.SiteName;
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
