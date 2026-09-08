using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Imaging;
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

    /// <summary>The pilot's ESI portrait for the card's hex — same source and pattern as the fleet roster leaf and
    /// the character picker (ET-184): reuse the existing portrait route rather than a new one. Null until loaded or
    /// when images are off/offline, so the hex falls back to the initial glyph below.</summary>
    [ObservableProperty] private Bitmap? _portrait;

    public bool HasPortrait => Portrait is not null;
    partial void OnPortraitChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPortrait));

    /// <summary>First letter of the name, shown in the hex when no portrait render is available — the same fallback
    /// as every other hex in the app, so "no ESI link", "images off" and "still loading" all read the same way
    /// instead of one of them looking like a broken image.</summary>
    public string Initial => string.IsNullOrEmpty(CharacterText) ? "?" : CharacterText[..1].ToUpperInvariant();

    /// <summary>Loads the ESI portrait best-effort (opt-in image setting); a failure leaves the glyph fallback.</summary>
    public async Task LoadPortraitAsync(ICharacterPortraitProvider portraits, CancellationToken cancellationToken = default)
    {
        if (Character.EsiCharacterId is not > 0)
            return;
        Portrait = await portraits.GetPortraitAsync(Character.EsiCharacterId.Value, 64, cancellationToken);
    }

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
