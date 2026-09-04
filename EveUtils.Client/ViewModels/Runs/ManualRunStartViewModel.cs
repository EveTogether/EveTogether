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
/// The manual entry to a run (ET-163): character, activity kind and a site picked from the SDE catalogue, started
/// through the same <see cref="StartRunCommand"/> the clipboard/signature flow in ActivityWindowViewModel uses —
/// this is the second production caller, not a second run type. The mission path is deliberately absent: its three
/// autocompletes need SDE data that is not imported yet (ET-129).
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

    [ObservableProperty] private ActivityKind _selectedActivityKind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SiteResults))]
    [NotifyPropertyChangedFor(nameof(HasSiteResults))]
    private string _siteQuery = string.Empty;

    public IReadOnlyList<SdeSite> SiteResults =>
        string.IsNullOrWhiteSpace(SiteQuery) ? [] : _sde.SearchSites(SiteQuery);

    public bool HasSiteResults => SiteResults.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedSite))]
    private SdeSite? _selectedSite;

    public bool HasSelectedSite => SelectedSite is not null;

    [ObservableProperty] private bool _isBackdated;

    [ObservableProperty] private DateTimeOffset? _backdatedDate = DateTimeOffset.Now;

    [ObservableProperty] private TimeSpan? _backdatedTime = DateTime.Now.TimeOfDay;

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private bool _statusIsError;

    public bool Started { get; private set; }

    /// <summary>Raised once the run exists — the dialog's cue to go, the same signal SdeProgress uses.</summary>
    public event Action? CloseRequested;

    private bool CanStart => SelectedCharacter is not null && SelectedSite is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (SelectedCharacter is not { EsiCharacterId: { } characterId } || SelectedSite is not { } site)
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

        Started = true;
        StatusIsError = false;

        // The dialog goes first: the activity window must not come up behind a modal that is still standing. What
        // it is handed is a plain view model, which adopts the run the store now has open by itself — the same
        // route the runs overview takes to a running lane, rather than a second one invented here.
        CloseRequested?.Invoke();
        _dialogs.ShowActivityWindow(_runWindowFor(SelectedActivityKind));
    }
}
