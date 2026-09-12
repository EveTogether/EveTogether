using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Notifications;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Grouping;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// The manual entry to a run (ET-163): character, activity kind and whatever that kind's own catalogue row asks for
/// — a site, an abyssal's nothing-here (tier and weather are the run window's own question), or a mission's typed
/// name — started through the same <see cref="StartRunCommand"/> the clipboard/signature flow in
/// ActivityWindowViewModel uses; this is the second production caller, not a second run type.
///
/// <see cref="ActivityKinds"/> and what each one asks for both come from <see cref="RunTypeCatalogue"/> (ET-255):
/// a kind with no catalogue row naming <see cref="ManualStartRequirement"/> never appears here at all, which is how
/// a mission stayed out before this ticket (its agent/level autocompletes need SDE data that is still not imported,
/// ET-129 — the name alone needs none) and how Mining stays out today (ET-229 has not given it an
/// <see cref="ActivityKind"/> to resolve from yet).
///
/// An abyssal asks for nothing here: a pocket is not in the site catalogue, so requiring one left START grey for
/// good, and the run it prepares does not begin running here — see <see cref="_PrepareAbyssalRun"/>.
///
/// There is no STARTTIME field. Starting now means the starttime is now; BACKDATE is the one exception, for a run
/// typed in after the fact.
///
/// It is a dialog, and it is done the moment the run exists: START hands over to the activity window through the
/// same <see cref="IDialogService.ShowActivityWindow"/> the clipboard route uses (ET-158), then closes itself — a
/// second way to put a run on screen would drift from that one.
///
/// One pilot plus additional characters, all under one group code (ET-221) — the same shape ET-210 chose for the
/// other three start paths, and the same one <see cref="EveUtils.Client.Runs.FleetRunWindowPresenter"/> hands a
/// member who accepts a commander's run with several clients up. The first character picked is the pilot; the
/// window is opened on them, and every other picked character gets its own row filed under the same code,
/// best-effort.
/// </summary>
public partial class ManualRunStartViewModel : ViewModelBase
{
    private readonly IDispatcher _dispatcher;
    private readonly ISdeAccessor _sde;
    private readonly IDialogService _dialogs;
    private readonly IToastService? _toasts;
    private readonly Func<ActivityKind, ActivityWindowViewModel> _runWindowFor;

    /// <param name="runWindowFor">Builds the run window this dialog hands over to. A delegate rather than the
    /// container: what that view model needs is its own business, and this one still says on its signature that a
    /// dispatcher, the catalogue and a way to open a window is the whole of what it takes.</param>
    /// <param name="preselectedCharacter">Ticked before the pilot looks at the dialog (ET-216), same as every other
    /// multi-select this app asks — opening from a character card starts that card's own character checked, never
    /// picked automatically. Null (Tools → Start run) leaves the first registered character as the starting point,
    /// same as before this dialog knew more than one.</param>
    public ManualRunStartViewModel(IDispatcher dispatcher, ISdeAccessor sde, IDialogService dialogs,
        Func<ActivityKind, ActivityWindowViewModel> runWindowFor, IReadOnlyList<Character> characters,
        Character? preselectedCharacter = null, IToastService? toasts = null)
    {
        _dispatcher = dispatcher;
        _sde = sde;
        _dialogs = dialogs;
        _toasts = toasts;
        _runWindowFor = runWindowFor;
        Characters = [.. characters.Where(character => character.EsiCharacterId is > 0)];
        Character? starting = Characters.FirstOrDefault(character => character.EsiCharacterId == preselectedCharacter?.EsiCharacterId)
            ?? Characters.FirstOrDefault();
        SelectedCharacters = starting is null ? [] : [starting];
        // The field directly, not the property: the setter's own OnSelectedActivityKindChanged persists a choice
        // (ET-255), and this default is not one — LoadAsync overwrites it with the remembered kind, if any, the
        // moment it can ask, and must not lose a race against this constructor rewriting it back to Site first.
        _selectedActivityKind = ActivityKind.Site;
    }

    /// <summary>Where the last picked kind is remembered (ET-255) — under <c>ui.</c> with the other shell prefs
    /// <see cref="EveUtils.Client.ViewModels.Runs.Sections.ActivityWindowSectionViewModel"/> already keeps there.
    /// Tier and weather need no key of their own here: they are the run window's own question, and
    /// <see cref="_PrepareAbyssalRun"/> hands the pilot straight to a window that already restores its own last
    /// answer from <c>ActivityWindowSectionViewModel.TierSettingKey</c>/<c>WeatherSettingKey</c> on
    /// <c>LoadAsync</c>, with nothing for this dialog to remember twice.</summary>
    public const string LastKindSettingKey = "ui.manual-start.activity-kind";

    /// <summary>Restores the last picked kind, once the dialog's own queries can run — separate from the
    /// constructor for the same reason <c>ActivityWindowViewModel.LoadAsync</c> is: a synchronous build that a test
    /// can assert against before anything async races it. A stored kind this build no longer offers (an older
    /// client's setting read by a build that dropped a kind, or simply none saved yet) leaves today's default,
    /// Site, standing.</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<Shared.Modules.Settings.Dtos.SettingDto> settings = await _dispatcher.Query(new GetSettingsQuery());
        string? stored = settings.FirstOrDefault(setting => setting.Key == LastKindSettingKey)?.Value;
        if (stored is not null
            && Enum.TryParse(stored, out ActivityKind kind)
            && ActivityKinds.Contains(kind))
            SelectedActivityKind = kind;
    }

    public IReadOnlyList<Character> Characters { get; }

    /// <summary>Who this run is for — the first entry is the pilot the window is opened on, the rest ride along
    /// under the same group code once START fires (ET-221). Defaults to the preselected or first character, exactly
    /// like the single-character picker this replaces, so hitting START without touching the picker keeps behaving
    /// the same for one character.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(CharacterSummaryText))]
    private IReadOnlyList<Character> _selectedCharacters = [];

    /// <summary>The header of the picker button — one name, or every picked name, the same shape
    /// <see cref="ActivityWindowViewModel.ActingCharacterText"/> already uses for a running group's chip.</summary>
    public string CharacterSummaryText => SelectedCharacters.Count switch
    {
        0 => "Pick character(s)…",
        1 => SelectedCharacters[0].Name,
        _ => string.Join(" · ", SelectedCharacters.Select(character => character.Name))
    };

    /// <summary>Reopens the same multi-select every other ET-210 start path uses — no second picking UI invented for
    /// this dialog. Dismissed leaves the current picks untouched, same as everywhere else this dialog is asked.
    ///
    /// Preselects whatever is already ticked in <see cref="SelectedCharacters"/> — not a fixed id captured once at
    /// construction — so reopening the picker after choosing several characters starts with all of them still
    /// ticked instead of only the very first one (Jithran, 2026-09-10: "als ik erop klik kan ik meerdere chars
    /// selecteren. als ik dan weer op dat veld klik is alles weer uitgevinkt"). The picker's own returned order
    /// follows the fixed candidate list, not click order (see <see cref="CharacterPickOption"/>'s callers), so
    /// re-ticking the same set reproduces the same order it returned last time — the first character stays the
    /// pilot across a reopen without any special-casing here.</summary>
    [RelayCommand]
    private async Task PickCharactersAsync()
    {
        IReadOnlyList<int> preselected = [.. SelectedCharacters
            .Where(character => character.EsiCharacterId is not null)
            .Select(character => character.EsiCharacterId!.Value)];

        IReadOnlyList<int>? picked = await _dialogs.PickCharactersAsync("Who is starting this run?",
            [.. Characters.Select(character => new CharacterPickOption(
                character.EsiCharacterId!.Value, character.Name, string.Empty, Enabled: true))],
            preselected);

        if (picked is not { Count: > 0 })
            return;

        SelectedCharacters = [.. picked
            .Select(id => Characters.FirstOrDefault(character => character.EsiCharacterId == id))
            .Where(character => character is not null)!];
    }

    /// <summary>Every kind this dialog may offer (ET-255) — the catalogue's own answer, not a list kept here that a
    /// new type could be forgotten from.</summary>
    public IReadOnlyList<ActivityKind> ActivityKinds { get; } = RunTypeCatalogue.ManuallyStartableKinds;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(IsAbyssal))]
    [NotifyPropertyChangedFor(nameof(NeedsSite))]
    [NotifyPropertyChangedFor(nameof(NeedsMissionName))]
    [NotifyPropertyChangedFor(nameof(CanBackdate))]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    private ActivityKind _selectedActivityKind;

    /// <summary>What the selected kind's own catalogue row asks this dialog for (ET-255) — the one place that
    /// decision is made; <see cref="IsAbyssal"/>, <see cref="NeedsSite"/> and <see cref="NeedsMissionName"/> below
    /// just name this fact's three answers, the way <c>ManualRunStartWindow.axaml</c> already expects.</summary>
    private ManualStartRequirement? _ManualStart => RunTypeCatalogue.For(SelectedActivityKind, null).ManualStart;

    public bool IsAbyssal => _ManualStart == ManualStartRequirement.Abyssal;

    /// <summary>Whether this kind is named by a site from the catalogue. Only a site is: an abyssal pocket is not in
    /// the catalogue at all, and a mission is named by a typed name instead.</summary>
    public bool NeedsSite => _ManualStart == ManualStartRequirement.Site;

    /// <summary>Whether this kind is named by a typed name rather than a catalogue site (ET-255) — a mission today.</summary>
    public bool NeedsMissionName => _ManualStart == ManualStartRequirement.MissionName;

    /// <summary>An abyssal run is not given a start time here — <see cref="_PrepareAbyssalRun"/> hands over a run
    /// that is not on the clock yet, so there is nothing for an earlier moment to move.</summary>
    public bool CanBackdate => !IsAbyssal;

    /// <summary>An abyssal starts no clock from this dialog, and a button that says otherwise is the whole reason
    /// this screen was misread.</summary>
    public string StartButtonText => IsAbyssal ? "PREPARE RUN" : "START RUN";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _missionName = string.Empty;

    // A half-filled site (or mission name) behind a field that is no longer on screen is how a hidden choice comes
    // back later: the kind decides what is asked, so changing it drops the answer to the question that is gone.
    // The choice itself is remembered (ET-255) — fire-and-forget, the same way every other settings write a plain
    // property setter causes in this app is (ActivityWindowSectionViewModel's own tier/weather persistence is the
    // one exception, because those come from a RelayCommand that can simply await it).
    partial void OnSelectedActivityKindChanged(ActivityKind value)
    {
        SiteQuery = string.Empty;
        SelectedOption = null;
        MissionName = string.Empty;
        _ = _dispatcher.Send(new SetSettingCommand(LastKindSettingKey, value.ToString()));
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

    private bool CanStart => SelectedCharacters.Count > 0
        && (!NeedsSite || SelectedSite is not null)
        && (!NeedsMissionName || !string.IsNullOrWhiteSpace(MissionName));

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (SelectedCharacters.Count == 0)
            return;

        Character pilot = SelectedCharacters[0];
        if (pilot.EsiCharacterId is not { } pilotCharacterId)
            return;

        // Everyone picked after the pilot (ET-221) — the same "first ticked is the pilot, the rest ride along"
        // convention FleetRunWindowPresenter._AcceptAsync and ActivityWindowViewModel._ResolveCharacterAsync
        // already use for their own multi-picks.
        IReadOnlyList<Character> additional = [.. SelectedCharacters.Skip(1)];

        if (IsAbyssal)
        {
            _PrepareAbyssalRun(pilotCharacterId, pilot.Name, additional);
            return;
        }

        // What names this run (ET-255): a catalogue site, same as before, or — for a mission — the typed name, with
        // no dungeon id to give (0, the same sentinel RunRewardStorageTests' own mission rows use) and its own
        // SiteTypeSource so SiteTypeId's id space reads correctly back (ET-137).
        int siteTypeId;
        string name;
        SiteTypeSource siteTypeSource;
        if (NeedsMissionName)
        {
            siteTypeId = 0;
            name = MissionName;
            siteTypeSource = SiteTypeSource.Mission;
        }
        else if (SelectedSite is { } site)
        {
            siteTypeId = site.DungeonId;
            name = site.Name;
            siteTypeSource = SiteTypeSource.Site;
        }
        else
            return;

        // "Earlier moment" types a past moment; without it the starttime is simply now. Either way this is the
        // only place StartedAtUtc is decided — nothing downstream corrects it (ET-163 AC-3: no measured start to
        // correct means TimesCorrectedAtUtc stays untouched).
        DateTime startedAtUtc = IsBackdated && BackdatedDate is { } date && BackdatedTime is { } time
            ? DateTime.SpecifyKind(date.Date + time, DateTimeKind.Local).ToUniversalTime()
            : DateTime.UtcNow;

        // More than one picked shares one group code, minted here exactly like ActivityWindowViewModel._StoreRunAsync
        // mints one for the clipboard flow's own multi-pick (ET-210) — a single character keeps today's plain,
        // code-less run (AC-3: one character picked is exactly today's behaviour).
        string? groupCode = additional.Count > 0 ? RunGroupCode.Create() : null;

        Result<Guid> result = await _dispatcher.Send(new StartRunCommand(
            pilotCharacterId,
            SelectedActivityKind,
            startedAtUtc,
            siteTypeId,
            name,
            SolarSystemId: null,
            GroupCode: groupCode,
            SiteTypeSource: siteTypeSource,
            Origin: RunOrigin.Manual,
            CharacterNameSnapshot: pilot.Name), cancellationToken);

        if (!result.IsSuccess)
        {
            Status = result.Messages.FirstOrDefault()?.Text ?? "Could not start this run.";
            StatusIsError = true;
            return;
        }

        // Every other picked character gets its own row under the same code, filed as though it started on its
        // own — best-effort, same as ActivityWindowViewModel._SendAdditionalStartRunCommandAsync: one extra
        // character failing to register is reported and does not undo the pilot's own run.
        foreach (Character extra in additional)
        {
            if (extra.EsiCharacterId is not { } extraId)
                continue;

            Result<Guid> extraResult = await _dispatcher.Send(new StartRunCommand(
                extraId,
                SelectedActivityKind,
                startedAtUtc,
                siteTypeId,
                name,
                SolarSystemId: null,
                GroupCode: groupCode,
                SiteTypeSource: siteTypeSource,
                Origin: RunOrigin.Manual,
                CharacterNameSnapshot: extra.Name), cancellationToken);

            if (!extraResult.IsSuccess)
                _toasts?.Show("A character was not added to this run",
                    extraResult.Messages.FirstOrDefault()?.Text ?? $"Could not start this run for {extra.Name}.",
                    ToastKind.Error);
        }

        Completed = true;
        StatusIsError = false;

        ActivityWindowViewModel window = _runWindowFor(SelectedActivityKind);
        // Named before the window loads (ET-221): _AdoptRunningRunAsync falls back to "the one run running
        // anywhere" only when it does not know its pilot yet, and that fallback turns ambiguous the moment a
        // second character's run exists here too — the same reason FleetRunWindowPresenter._Open always names
        // its own pilot rather than leaving the window to guess.
        window.UseCharacter(pilotCharacterId, pilot.Name);

        // The dialog goes first: the activity window must not come up behind a modal that is still standing.
        CloseRequested?.Invoke();
        _dialogs.ShowActivityWindow(window);
    }

    /// <summary>
    /// Hand over an abyssal run standing by: no <see cref="StartRunCommand"/>, so no row, no start time, no clock.
    /// You fire the filament long after setting the run up, and a clock started here would have spent minutes of a
    /// twenty-minute limit while still docked. START or the location watch is what sets it going.
    ///
    /// Multi-character (ET-221) rides the mechanism already built for exactly this — a run that does not exist
    /// yet: <see cref="ActivityWindowViewModel.UseAdditionalCharacters"/> is read only once this window's own run
    /// is finally stored, whenever that turns out to be. No separate multi-toon path is built for an abyssal.
    /// </summary>
    private void _PrepareAbyssalRun(int characterId, string characterName, IReadOnlyList<Character> additional)
    {
        Completed = true;
        StatusIsError = false;

        ActivityWindowViewModel window = _runWindowFor(ActivityKind.Abyssal);
        // The pilot travels with it, so the window does not ask again for what this dialog already settled.
        window.UseCharacter(characterId, characterName);
        if (additional.Count > 0)
            window.UseAdditionalCharacters(
                [.. additional.Where(character => character.EsiCharacterId is not null)
                    .Select(character => (character.EsiCharacterId!.Value, character.Name))]);
        CloseRequested?.Invoke();
        _dialogs.ShowActivityWindow(window);
    }
}
