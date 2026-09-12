using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Control;
using EveUtils.Shared.Modules.Runs.Dtos;
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
/// a kind with no catalogue row naming <see cref="ManualStartRequirement"/> never appears here at all. Mission's
/// name is autocompleted against the SDE (ET-173, ET-265) but stays freely typed either way, and Mining (ET-229)
/// asks for nothing beyond character(s) and a moment — measured (ET-265): the catalogue already marked Mining
/// startable (<see cref="ManualStartRequirement.None"/>) before this ticket, but <see cref="ActivityKind"/> had no
/// member for it to resolve from, so it never reached <see cref="ActivityKinds"/> at all.
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
/// <summary>One row of the fleet choice offered before START (ET-267): solo (<see cref="FleetId"/> null), or a fleet
/// the pilot is actively in right now, named for the button.</summary>
public sealed record FleetStartOption(long? FleetId, string Label);

public partial class ManualRunStartViewModel : ViewModelBase
{
    private readonly IDispatcher _dispatcher;
    private readonly ISdeAccessor _sde;
    private readonly IDialogService _dialogs;
    private readonly IToastService? _toasts;
    private readonly IFleetParticipation? _fleetParticipation;
    private readonly ILocalCharacterPresence? _localPresence;
    private readonly Func<ActivityKind, ActivityWindowViewModel> _runWindowFor;

    /// <summary>The anchor <see cref="LoadAsync"/> keys the fleet-first default and the remembered pick on (ET-270,
    /// see <see cref="EveUtils.Client.Runs.OwnCharacterPickMemory"/>) — null when this dialog was opened for a
    /// specific character's own card, so <see cref="LoadAsync"/> leaves that card's single character standing
    /// rather than silently ticking others alongside it (see <paramref name="preselectedCharacter"/> on the
    /// constructor: a card's own START has never picked anyone automatically, and restoring a fleet or a remembered
    /// team would be exactly that). Tools → Start run has no card to honour, so that entry gets the full default.</summary>
    private readonly int? _restoreAnchorCharacterId;

    /// <summary>The always-first row of <see cref="FleetOptions"/>: no <see cref="StartRunCommand.FleetId"/>, exactly
    /// today's behaviour. Its label doubles as "own characters" for a multi-pick, since neither ever announces
    /// anything to a fleet.</summary>
    private static readonly FleetStartOption SoloOption = new(null, "Solo / own characters");

    /// <param name="runWindowFor">Builds the run window this dialog hands over to. A delegate rather than the
    /// container: what that view model needs is its own business, and this one still says on its signature that a
    /// dispatcher, the catalogue and a way to open a window is the whole of what it takes.</param>
    /// <param name="preselectedCharacter">Ticked before the pilot looks at the dialog (ET-216), same as every other
    /// multi-select this app asks — opening from a character card starts that card's own character checked, never
    /// picked automatically. Null (Tools → Start run) leaves the first registered character as the starting point,
    /// same as before this dialog knew more than one.</param>
    /// <param name="fleetParticipation">The same live membership set <c>ActivityWindowViewModel</c> reads its own
    /// FleetId and commander from (ET-147/ET-152) — null in a test that never wires one, which leaves the pilot with
    /// no fleet choice, same as before this ticket.</param>
    /// <param name="localPresence">Who is logged in right now (ET-270) — the same "EVE client running" verdict the
    /// clipboard multi-select's own hint text reads off, and what keeps a restored pick from silently ticking a
    /// character who has since logged out. Null in a test that never wires one restores nothing, same as no
    /// characters being flying at all.</param>
    public ManualRunStartViewModel(IDispatcher dispatcher, ISdeAccessor sde, IDialogService dialogs,
        Func<ActivityKind, ActivityWindowViewModel> runWindowFor, IReadOnlyList<Character> characters,
        Character? preselectedCharacter = null, IToastService? toasts = null,
        IFleetParticipation? fleetParticipation = null, ILocalCharacterPresence? localPresence = null)
    {
        _dispatcher = dispatcher;
        _sde = sde;
        _dialogs = dialogs;
        _toasts = toasts;
        _fleetParticipation = fleetParticipation;
        _localPresence = localPresence;
        _runWindowFor = runWindowFor;
        Characters = [.. characters.Where(character => character.EsiCharacterId is > 0)];
        Character? starting = Characters.FirstOrDefault(character => character.EsiCharacterId == preselectedCharacter?.EsiCharacterId)
            ?? Characters.FirstOrDefault();
        _restoreAnchorCharacterId = preselectedCharacter is null ? starting?.EsiCharacterId : null;
        // The field directly, not the property: the setter's own OnSelectedActivityKindChanged persists a choice
        // (ET-255), and this default is not one — LoadAsync overwrites it with the remembered kind, if any, the
        // moment it can ask, and must not lose a race against this constructor rewriting it back to Site first.
        _selectedActivityKind = ActivityKind.Site;
        _selectedFleetOption = SoloOption;
        SelectedCharacters = starting is null ? [] : [starting];
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

        // ET-270: same fleet-first priority as the clipboard offers (OwnCharacterPickMemory) — skipped outright
        // when this dialog was opened for a specific character's own card (see _restoreAnchorCharacterId).
        if (_restoreAnchorCharacterId is { } anchor)
        {
            IReadOnlyCollection<int> flying = [.. InGameCharacters.Among(Characters, _localPresence)
                .Select(character => character.EsiCharacterId!.Value)];
            IReadOnlyList<FleetParticipant> participation = _fleetParticipation?.Current ?? [];
            long? fleetId = OwnCharacterPickMemory.FleetIdFor(anchor, participation);
            IReadOnlyCollection<int>? fleetCharacterIds = OwnCharacterPickMemory.FleetCharacterIdsFor(anchor, participation);
            IReadOnlyList<int>? restored = await OwnCharacterPickMemory.ResolvePreselectionAsync(
                _dispatcher, anchor, flying, fleetCharacterIds, fleetId);
            if (restored is { Count: > 0 })
                SelectedCharacters = [.. restored
                    .Select(id => Characters.FirstOrDefault(character => character.EsiCharacterId == id))
                    .Where(character => character is not null)!];
        }
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

    /// <summary>Every fleet the pilot (the first picked character) is actively in right now, <see cref="SoloOption"/>
    /// always first — rebuilt whenever the pilot changes (ET-267). Sourced from <see cref="IFleetParticipation"/>,
    /// the same live membership set <c>ActivityWindowViewModel</c> already reads its own FleetId and commander from
    /// (ET-147/ET-152), so a fleet only appears here once it actually broadcasts (started, not merely forming).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFleetOptions))]
    [NotifyPropertyChangedFor(nameof(ShowFleetChoice))]
    private IReadOnlyList<FleetStartOption> _fleetOptions = [SoloOption];

    /// <summary>Whether the pilot is in any fleet at all — the choice is offered only then (ET-267): flying nobody's
    /// fleet leaves nothing to choose between, and today's solo/own-characters behaviour stands unannounced.</summary>
    public bool HasFleetOptions => FleetOptions.Count > 1;

    /// <summary>An abyssal's own START does not reach <see cref="StartRunCommand"/> at all — <see cref="_PrepareAbyssalRun"/>
    /// hands over a run that has not started yet, and the window it opens on already resolves its own live fleet
    /// membership the moment it does (ET-265). Offering a choice here that <see cref="StartAsync"/> never reads from
    /// for that kind would be a control that looks like it does something and does not.</summary>
    public bool ShowFleetChoice => HasFleetOptions && !IsAbyssal;

    [ObservableProperty] private FleetStartOption _selectedFleetOption = SoloOption;

    /// <summary>Rebuilds <see cref="FleetOptions"/> for whoever is now the pilot, and resets the choice to
    /// <see cref="SoloOption"/> rather than carrying an old pick across — a fleet chosen for one pilot means nothing
    /// for another, and picking is never guessed at (ET-201).</summary>
    partial void OnSelectedCharactersChanged(IReadOnlyList<Character> value)
    {
        int? pilotId = value.Count > 0 ? value[0].EsiCharacterId : null;
        List<FleetStartOption> options = [SoloOption];
        if (pilotId is { } characterId)
            options.AddRange((_fleetParticipation?.Current ?? [])
                .Where(participant => participant.CharacterId == characterId)
                .Select(participant => new FleetStartOption(
                    participant.FleetId, $"With fleet {participant.FleetName ?? $"#{participant.FleetId}"}")));
        FleetOptions = options;
        SelectedFleetOption = SoloOption;
    }

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

        // ET-270: remember this exact pick, same as the clipboard offers — the newly picked pilot's own fleet (if
        // any) gets its exclusions updated, and every picked character's own last-full-pick row is kept fresh as
        // the no-fleet fallback.
        IReadOnlyList<FleetParticipant> participation = _fleetParticipation?.Current ?? [];
        long? pilotFleetId = OwnCharacterPickMemory.FleetIdFor(picked[0], participation);
        IReadOnlyCollection<int>? pilotFleetCharacterIds = OwnCharacterPickMemory.FleetCharacterIdsFor(picked[0], participation);
        await OwnCharacterPickMemory.SaveAsync(_dispatcher, picked, pilotFleetCharacterIds, pilotFleetId);
    }

    /// <summary>Every kind this dialog may offer (ET-255) — the catalogue's own answer, not a list kept here that a
    /// new type could be forgotten from.</summary>
    public IReadOnlyList<ActivityKind> ActivityKinds { get; } = RunTypeCatalogue.ManuallyStartableKinds;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(IsAbyssal))]
    [NotifyPropertyChangedFor(nameof(NeedsSite))]
    [NotifyPropertyChangedFor(nameof(NeedsMissionName))]
    [NotifyPropertyChangedFor(nameof(HasOptionalLocationName))]
    [NotifyPropertyChangedFor(nameof(CanBackdate))]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    [NotifyPropertyChangedFor(nameof(ShowFleetChoice))]
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

    /// <summary>Whether this kind asks for nothing beyond character(s) and a moment (ET-229, ET-265) — Mining
    /// today, the one row whose <see cref="RunTypeDefinition.ManualStart"/> is
    /// <see cref="ManualStartRequirement.None"/>. Unlike a site or a mission, naming it is optional: a belt or a
    /// system typed in is a courtesy, not something <see cref="CanStart"/> waits on.</summary>
    public bool HasOptionalLocationName => _ManualStart == ManualStartRequirement.None;

    /// <summary>An abyssal run is not given a start time here — <see cref="_PrepareAbyssalRun"/> hands over a run
    /// that is not on the clock yet, so there is nothing for an earlier moment to move.</summary>
    public bool CanBackdate => !IsAbyssal;

    /// <summary>An abyssal starts no clock from this dialog, and a button that says otherwise is the whole reason
    /// this screen was misread.</summary>
    public string StartButtonText => IsAbyssal ? "PREPARE RUN" : "START RUN";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(MissionResults))]
    [NotifyPropertyChangedFor(nameof(HasMissionResults))]
    private string _missionName = string.Empty;

    /// <summary>Substring matches against the SDE <c>Mission</c> table (2,892 rows, ET-173) as the pilot types
    /// <see cref="MissionName"/> — the same live-search shape <see cref="SiteResults"/> gives the site picker
    /// (ET-265). Unlike a site, picking a suggestion is a convenience, not a requirement: <see cref="MissionName"/>
    /// stays a freely typed field either way, since a mission's own name has never needed a catalogue match to
    /// start (ET-255).</summary>
    public IReadOnlyList<SdeMission> MissionResults =>
        string.IsNullOrWhiteSpace(MissionName) ? [] : _sde.SearchMissions(MissionName);

    public bool HasMissionResults => MissionResults.Count > 0;

    [ObservableProperty] private SdeMission? _selectedMissionResult;

    /// <summary>Overwrites <see cref="MissionName"/> with the picked row's own name — the one thing a mission's
    /// catalogue entry can fill in (ET-265 measured: <c>Mission</c> carries no level at all, that lives on the
    /// agent who hands it out, and no player-facing "kind" survived ET-173's deliberately minimal import — only the
    /// agent's own classification did, which is a different fact). Level comes from <see cref="MissionAgentName"/>
    /// instead, exactly as it already does for a clipboard-started mission.</summary>
    partial void OnSelectedMissionResultChanged(SdeMission? value)
    {
        if (value is not null)
            MissionName = value.Name;
    }

    /// <summary>The agent behind the mission — optional (ET-265), resolved by exact name at START the same way
    /// <c>ClipboardMissionOffer</c> already resolves one from a capture's "Report to" line. This is the only source
    /// this dialog has for the mission's level: <see cref="MissionResults"/>' own catalogue rows carry none.</summary>
    [ObservableProperty] private string _missionAgentName = string.Empty;

    [ObservableProperty] private decimal? _missionIsk;

    [ObservableProperty] private decimal? _missionBonusIsk;

    /// <summary>How much of the bonus window is left as of right now (ET-237: the timer counts down from what
    /// remains, not from the mission's own accept-time window) — stored as <c>ObservedAtUtc = now, BonusWindowSeconds
    /// = this</c>, the same two fields the MISSION section already reads the deadline from.</summary>
    [ObservableProperty] private TimeSpan? _missionBonusWindowRemaining;

    [ObservableProperty] private decimal? _missionLoyaltyPoints;

    [ObservableProperty] private string _missionItemName = string.Empty;

    [ObservableProperty] private decimal? _missionItemQuantity;

    /// <summary>Named by a belt or a system, typed in rather than picked from a catalogue (ET-229: mining has no
    /// site behind it at all) — optional, unlike <see cref="MissionName"/> or a picked <see cref="SelectedSite"/>,
    /// neither of which this dialog will start without.</summary>
    [ObservableProperty] private string _locationName = string.Empty;

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
        MissionAgentName = string.Empty;
        MissionIsk = null;
        MissionBonusIsk = null;
        MissionBonusWindowRemaining = null;
        MissionLoyaltyPoints = null;
        MissionItemName = string.Empty;
        MissionItemQuantity = null;
        LocationName = string.Empty;
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

        // What names this run (ET-255): a catalogue site, same as before, a mission's typed name, or — for Mining
        // (ET-229, ET-265) — an optional belt/system typed in, with no catalogue behind it at all. Either of the
        // last two gives no dungeon id (0, the same sentinel RunRewardStorageTests' own mission rows use), and its
        // own SiteTypeSource so SiteTypeId's id space reads correctly back (ET-137).
        int siteTypeId;
        string? name;
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
        else if (HasOptionalLocationName)
        {
            siteTypeId = 0;
            name = string.IsNullOrWhiteSpace(LocationName) ? null : LocationName.Trim();
            siteTypeSource = SiteTypeSource.Uncatalogued;
        }
        else
            return;

        // The mission's own facts (ET-172, ET-265): an agent typed in is optional, and the only source this dialog
        // has for the mission's level — MissionResults' own catalogue rows carry none (measured: the SDE Mission
        // table is name-and-keys-only, ET-173). Null on every kind but Mission, same as the clipboard path.
        SdeAgent? agent = NeedsMissionName && !string.IsNullOrWhiteSpace(MissionAgentName)
            ? _sde.FindAgentByName(MissionAgentName)
            : null;
        IReadOnlyList<RunParameterInput> rewardParameters = NeedsMissionName ? _BuildMissionRewardParameters() : [];

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

        // The one thing that turns this start into a shared, fleet-offered run (ET-267, ET-147, ET-152): the pilot's
        // own explicit choice, never guessed at. Whether it makes this pilot the commander is not decided here —
        // that is RunControlAuthority's job, off the same live membership ActivityWindowViewModel already reads its
        // own FleetId/IsFleetCommander from — this dialog only ever answers "am I in that fleet's roster right now",
        // never "am I the boss".
        long? fleetId = SelectedFleetOption.FleetId;
        int? fleetCommanderCharacterId = fleetId is { } lookupFleetId
            ? (_fleetParticipation?.Current ?? [])
                .FirstOrDefault(participant => participant.CharacterId == pilotCharacterId && participant.FleetId == lookupFleetId)
                .FleetCommanderCharacterId
            : null;
        bool isFleetCommander = fleetId is not null
            && RunControlAuthority.From(fleetId, fleetCommanderCharacterId, pilotCharacterId, groupCode: null).IsFleetCommander;

        Result<Guid> result = await _dispatcher.Send(new StartRunCommand(
            pilotCharacterId,
            SelectedActivityKind,
            startedAtUtc,
            siteTypeId,
            name,
            SolarSystemId: agent?.SolarSystemId,
            GroupCode: groupCode,
            SiteTypeSource: siteTypeSource,
            Origin: RunOrigin.Manual,
            CharacterNameSnapshot: pilot.Name,
            AgentId: agent?.AgentId,
            MissionLevel: agent?.Level,
            FleetId: fleetId,
            IsFleetCommander: isFleetCommander,
            // Only the pilot's own run carries the typed-in rewards (ET-260): in EVE a mission's reward is paid to
            // one character, never duplicated across whoever else rode along.
            Parameters: rewardParameters), cancellationToken);

        if (!result.IsSuccess)
        {
            Status = result.Messages.FirstOrDefault()?.Text ?? "Could not start this run.";
            StatusIsError = true;
            return;
        }

        // Every other picked character gets its own row under the same code, filed as though it started on its
        // own — best-effort, same as ActivityWindowViewModel._SendAdditionalStartRunCommandAsync: one extra
        // character failing to register is reported and does not undo the pilot's own run. Never the fleet
        // commander (only the acting character ever is, same rule ActivityWindowViewModel's own additional-character
        // start uses) — but the same FleetId, so its run is filed the same way and shares in the same offer.
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
                SolarSystemId: agent?.SolarSystemId,
                GroupCode: groupCode,
                SiteTypeSource: siteTypeSource,
                Origin: RunOrigin.Manual,
                CharacterNameSnapshot: extra.Name,
                AgentId: agent?.AgentId,
                MissionLevel: agent?.Level,
                FleetId: fleetId,
                IsFleetCommander: false), cancellationToken);

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

    /// <summary>The typed-in rewards, in the same <see cref="RunParameterInput"/> shapes a clipboard mission copy
    /// produces (ET-172, ET-265) — so the MISSION section reads a hand-entered run exactly the way it reads one
    /// <c>ClipboardMissionOffer</c> started. Each line is skipped rather than added half-filled: an amount typed
    /// with nothing else missing (the bonus's own window) does not become a reward that cannot be judged.</summary>
    private List<RunParameterInput> _BuildMissionRewardParameters()
    {
        DateTime now = DateTime.UtcNow;
        var parameters = new List<RunParameterInput>();
        if (MissionIsk is { } isk && isk > 0)
            parameters.Add(new RunParameterInput
            {
                ParameterKey = RunParameterKey.Isk,
                TypedValue = FormattableString.Invariant($"{isk} ISK"),
                Amount = isk,
                ObservedAtUtc = now
            });

        // The window is what remains right now (ET-237: the timer counts down from what is left, not from the
        // mission's own accept-time length, confirmed by Jithran) — so ObservedAtUtc is this moment, and the
        // deadline (ObservedAtUtc + BonusWindowSeconds) lands exactly on what was typed in.
        if (MissionBonusIsk is { } bonus && bonus > 0 && MissionBonusWindowRemaining is { } remaining && remaining > TimeSpan.Zero)
            parameters.Add(new RunParameterInput
            {
                ParameterKey = RunParameterKey.BonusIsk,
                TypedValue = FormattableString.Invariant($"{bonus} ISK"),
                Amount = bonus,
                BonusWindowSeconds = (int)remaining.TotalSeconds,
                ObservedAtUtc = now
            });

        if (MissionLoyaltyPoints is { } lp && lp > 0)
            parameters.Add(new RunParameterInput
            {
                ParameterKey = RunParameterKey.LoyaltyPoints,
                TypedValue = FormattableString.Invariant($"{lp} LP"),
                Amount = lp,
                ObservedAtUtc = now
            });

        if (!string.IsNullOrWhiteSpace(MissionItemName))
        {
            decimal quantity = MissionItemQuantity is { } typed && typed > 0 ? typed : 1;
            parameters.Add(new RunParameterInput
            {
                ParameterKey = RunParameterKey.Item,
                TypedValue = FormattableString.Invariant($"{quantity} x {MissionItemName}"),
                Amount = quantity,
                ItemTypeId = _sde.TryGetTypeId(MissionItemName, out int typeId) ? typeId : null,
                ObservedAtUtc = now
            });
        }
        return parameters;
    }
}
