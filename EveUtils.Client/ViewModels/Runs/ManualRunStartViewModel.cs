using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// The manual entry to a run (ET-163): character, activity kind and — for a site — one picked from the SDE
/// catalogue, started through the same <see cref="StartRunCommand"/> the clipboard/signature flow in
/// ActivityWindowViewModel uses; this is the second production caller, not a second run type. The mission path is
/// deliberately absent: its three autocompletes need SDE data that is not imported yet (ET-129).
///
/// An abyssal asks for neither: a pocket is not in the site catalogue, so requiring one left START grey for good,
/// and the run it prepares does not begin running here — see <see cref="_PrepareAbyssalRun"/>.
///
/// There is no STARTTIME field. Starting now means the starttime is now; BACKDATE is the one exception, for a run
/// typed in after the fact.
///
/// It is a dialog, and it is done the moment the run exists: START hands over to the activity window through the
/// same <see cref="IDialogService.ShowActivityWindow"/> the clipboard route uses (ET-158), then closes itself — a
/// second way to put a run on screen would drift from that one.
/// </summary>
public partial class ManualRunStartViewModel : ViewModelBase
{
    private readonly IDispatcher _dispatcher;
    private readonly ISdeAccessor _sde;
    private readonly IDialogService _dialogs;
    private readonly Func<ActivityKind, ActivityWindowViewModel> _runWindowFor;

    /// <param name="runWindowFor">Builds the run window this dialog hands over to. A delegate rather than the
    /// container: what that view model needs is its own business, and this one still says on its signature that a
    /// dispatcher, the catalogue and a way to open a window is the whole of what it takes.</param>
    public ManualRunStartViewModel(IDispatcher dispatcher, ISdeAccessor sde, IDialogService dialogs,
        Func<ActivityKind, ActivityWindowViewModel> runWindowFor, IReadOnlyList<Character> characters)
    {
        _dispatcher = dispatcher;
        _sde = sde;
        _dialogs = dialogs;
        _runWindowFor = runWindowFor;
        Characters = [.. characters.Where(character => character.EsiCharacterId is > 0)];
        SelectedCharacter = Characters.FirstOrDefault();
        SelectedActivityKind = ActivityKind.Site;
    }

    public IReadOnlyList<Character> Characters { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private Character? _selectedCharacter;

    public IReadOnlyList<ActivityKind> ActivityKinds { get; } = [ActivityKind.Site, ActivityKind.Abyssal];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(IsAbyssal))]
    [NotifyPropertyChangedFor(nameof(NeedsSite))]
    [NotifyPropertyChangedFor(nameof(CanBackdate))]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    private ActivityKind _selectedActivityKind;

    public bool IsAbyssal => SelectedActivityKind is ActivityKind.Abyssal;

    /// <summary>Whether this kind is named by a site from the catalogue. Only a site is: an abyssal pocket is not in
    /// the catalogue at all, and a mission is named by its agent.</summary>
    public bool NeedsSite => SelectedActivityKind is ActivityKind.Site;

    /// <summary>An abyssal run is not given a start time here — <see cref="_PrepareAbyssalRun"/> hands over a run
    /// that is not on the clock yet, so there is nothing for an earlier moment to move.</summary>
    public bool CanBackdate => !IsAbyssal;

    /// <summary>An abyssal starts no clock from this dialog, and a button that says otherwise is the whole reason
    /// this screen was misread.</summary>
    public string StartButtonText => IsAbyssal ? "PREPARE RUN" : "START RUN";

    // A half-filled site behind a field that is no longer on screen is how a hidden choice comes back later: the
    // kind decides what is asked, so changing it drops the answer to the question that is gone.
    partial void OnSelectedActivityKindChanged(ActivityKind value)
    {
        SiteQuery = string.Empty;
        SelectedOption = null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SiteResults))]
    [NotifyPropertyChangedFor(nameof(HasSiteResults))]
    private string _siteQuery = string.Empty;

    /// <summary>Built through <see cref="SdeSitePickerOption.From"/> — the one presentation this picker shares with
    /// <see cref="EscalationDialogViewModel"/>'s, so two rows sharing a name are never two unpickable, identical-
    /// looking duplicates (Raymond, 2026-09-05).</summary>
    public IReadOnlyList<SdeSitePickerOption> SiteResults =>
        string.IsNullOrWhiteSpace(SiteQuery) ? [] : SdeSitePickerOption.From(_sde.SearchSites(SiteQuery));

    public bool HasSiteResults => SiteResults.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedSite))]
    [NotifyPropertyChangedFor(nameof(HasSelectedSite))]
    private SdeSitePickerOption? _selectedOption;

    /// <summary>The site behind the picked option — what <see cref="StartAsync"/> reads; the label in
    /// <see cref="SelectedOption"/> is display-only.</summary>
    public SdeSite? SelectedSite => SelectedOption?.Site;

    public bool HasSelectedSite => SelectedOption is not null;

    [ObservableProperty] private bool _isBackdated;

    [ObservableProperty] private DateTimeOffset? _backdatedDate = DateTimeOffset.Now;

    [ObservableProperty] private TimeSpan? _backdatedTime = DateTime.Now.TimeOfDay;

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private bool _statusIsError;

    /// <summary>The dialog did what it was opened to do and the activity window has the run. Not "a clock is
    /// running": an abyssal is handed over standing by, which is the whole point of <see cref="_PrepareAbyssalRun"/>.</summary>
    public bool Completed { get; private set; }

    /// <summary>Raised once the run exists — the dialog's cue to go, the same signal SdeProgress uses.</summary>
    public event Action? CloseRequested;

    private bool CanStart => SelectedCharacter is not null && (!NeedsSite || SelectedSite is not null);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (SelectedCharacter is not { EsiCharacterId: { } characterId, Name: { } characterName })
            return;

        if (IsAbyssal)
        {
            _PrepareAbyssalRun(characterId, characterName);
            return;
        }

        if (SelectedSite is not { } site)
            return;

        // "Earlier moment" types a past moment; without it the starttime is simply now. Either way this is the
        // only place StartedAtUtc is decided — nothing downstream corrects it (ET-163 AC-3: no measured start to
        // correct means TimesCorrectedAtUtc stays untouched).
        DateTime startedAtUtc = IsBackdated && BackdatedDate is { } date && BackdatedTime is { } time
            ? DateTime.SpecifyKind(date.Date + time, DateTimeKind.Local).ToUniversalTime()
            : DateTime.UtcNow;

        Result<Guid> result = await _dispatcher.Send(new StartRunCommand(
            characterId,
            SelectedActivityKind,
            startedAtUtc,
            site.DungeonId,
            site.Name,
            SolarSystemId: null,
            SiteTypeSource: SiteTypeSource.Site,
            Origin: RunOrigin.Manual), cancellationToken);

        if (!result.IsSuccess)
        {
            Status = result.Messages.FirstOrDefault()?.Text ?? "Could not start this run.";
            StatusIsError = true;
            return;
        }

        Completed = true;
        StatusIsError = false;

        // The dialog goes first: the activity window must not come up behind a modal that is still standing. What
        // it is handed is a plain view model, which adopts the run the store now has open by itself — the same
        // route the runs overview takes to a running lane, rather than a second one invented here.
        CloseRequested?.Invoke();
        _dialogs.ShowActivityWindow(_runWindowFor(SelectedActivityKind));
    }

    /// <summary>
    /// Hand over an abyssal run standing by: no <see cref="StartRunCommand"/>, so no row, no start time, no clock.
    /// You fire the filament long after setting the run up, and a clock started here would have spent minutes of a
    /// twenty-minute limit while still docked. START or the location watch is what sets it going.
    /// </summary>
    private void _PrepareAbyssalRun(int characterId, string characterName)
    {
        Completed = true;
        StatusIsError = false;

        ActivityWindowViewModel window = _runWindowFor(ActivityKind.Abyssal);
        // The pilot travels with it, so the window does not ask again for what this dialog already settled.
        window.UseCharacter(characterId, characterName);
        CloseRequested?.Invoke();
        _dialogs.ShowActivityWindow(window);
    }
}
