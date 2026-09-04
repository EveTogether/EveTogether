using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// There is no STARTTIME field. Starting now means the starttime is now; ACHTERAF INVOEREN is the one exception,
/// for a run typed in after the fact.
/// </summary>
public partial class ManualRunStartViewModel : ViewModelBase
{
    private readonly IDispatcher _dispatcher;
    private readonly ISdeAccessor _sde;

    public ManualRunStartViewModel(IDispatcher dispatcher, ISdeAccessor sde, IReadOnlyList<Character> characters)
    {
        _dispatcher = dispatcher;
        _sde = sde;
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

    private bool CanStart => SelectedCharacter is not null && SelectedSite is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (SelectedCharacter is not { EsiCharacterId: { } characterId } || SelectedSite is not { } site)
            return;

        // "Terug in de tijd" types a past moment; without it the starttime is simply now. Either way this is the
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
        Status = $"Run started on {SelectedCharacter.Name} at {site.Name}.";
        StatusIsError = false;
    }
}
