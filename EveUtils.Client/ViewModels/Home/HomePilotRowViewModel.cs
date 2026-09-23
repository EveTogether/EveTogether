using System;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Esi;
using EveUtils.Client.Formatting;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Location;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.Home;

/// <summary>
/// One own character on the home (ET-324): on this PC or not, the system, the ship with its detected fit, the skill in
/// training and what the character made today and this month.
///
/// <para><b>Every scope-bound field is checked per character.</b> A field whose scope the pilot chose not to share
/// shows "not shared" and an ALLOW… that re-authorises with that scope ticked (D-21) — never a dash or a zero that would
/// read as data. It is a choice, not an error: no amber, nothing in the attention band.</para>
///
/// <para><b>"Not on this PC", never "offline".</b> Presence is the local client probe, which cannot see another
/// machine; with no client here there is no system and no ship to show either (ET-71).</para>
/// </summary>
public sealed partial class HomePilotRowViewModel : ObservableObject
{
    private readonly HomeNavigation _navigation;
    private SkillQueueStanding? _queue;
    private bool _hasQueueRead;

    public HomePilotRowViewModel(CharacterViewModel character, HomeNavigation navigation)
    {
        _navigation = navigation;
        _character = character;
        ShowPresence();
    }

    /// <summary>The shell's own row for this character: portrait, presence and ESI state stay live there. Swapped when
    /// the shell rebuilds its list, which is also when granted scopes change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharesLocation))]
    [NotifyPropertyChangedFor(nameof(SharesShip))]
    [NotifyPropertyChangedFor(nameof(SharesQueue))]
    private CharacterViewModel _character;

    public int CharacterId => Character.CharacterId;

    public bool SharesLocation => _Shares(LocationScopeCatalog.ReadLocation);

    public bool SharesShip => _Shares(LocationScopeCatalog.ReadShipType);

    public bool SharesQueue => _Shares(SkillsScopeCatalog.ReadSkillQueue);

    // ── WHERE ────────────────────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isOnThisPc;
    [ObservableProperty] private string? _systemName;
    [ObservableProperty] private string _systemDetailText = string.Empty;
    [ObservableProperty] private WhereState _where = WhereState.NotOnThisPc;

    // ── FLYING ───────────────────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private FlyingState _flying = FlyingState.NotOnThisPc;
    [ObservableProperty] private string _hullText = string.Empty;
    [ObservableProperty] private string _fitText = string.Empty;
    [ObservableProperty] private string _fitTooltip = string.Empty;
    [ObservableProperty] private Bitmap? _hullIcon;

    // ── TRAINING ─────────────────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private TrainingState _training = TrainingState.Unknown;
    [ObservableProperty] private string _skillText = string.Empty;
    [ObservableProperty] private string _trainingDetailText = string.Empty;
    [ObservableProperty] private string _trainingTooltip = string.Empty;
    [ObservableProperty] private bool _isQueueShort;
    [ObservableProperty] private double _queueBarFraction;

    // ── ISK ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _iskTodayText = "—";
    [ObservableProperty] private bool _hasIskToday;
    [ObservableProperty] private string _iskMonthText = "—";
    [ObservableProperty] private bool _hasIskMonth;

    public bool IsQueuePaused => _queue?.IsPaused ?? false;

    public SkillQueueStanding? Queue => _queue;

    partial void OnCharacterChanged(CharacterViewModel value) => ShowPresence();

    /// <summary>Presence, and with it whether there is a system and a ship to show at all.</summary>
    internal void ShowPresence()
    {
        IsOnThisPc = Character.HasActiveClient;
        _ShowWhere();
        if (!IsOnThisPc)
            Flying = FlyingState.NotOnThisPc;
        else if (!SharesShip)
            Flying = FlyingState.NotShared;
        else if (Flying == FlyingState.NotOnThisPc || Flying == FlyingState.NotShared)
            Flying = FlyingState.Unknown;
        _ShowTraining(DateTimeOffset.UtcNow);
    }

    /// <summary>The system from this PC's game log, and its security from the SDE (resolved by the caller, cached).</summary>
    internal void ShowSystem(string? systemName, double? security)
    {
        SystemName = systemName;
        SystemDetailText = security is { } sec
            ? $"{RunRowFacts.SecurityText(sec)} · on this PC"
            : "on this PC";
        _ShowWhere();
    }

    /// <summary>What <see cref="ShipFitDetectionService"/> last saw. Only drawn for a client on this PC with the ship
    /// scope shared; the reading of anyone else is left alone.</summary>
    internal void ShowShip(ShipFitDetectionReading reading, string hullName)
    {
        if (!IsOnThisPc || !SharesShip)
            return;

        if (reading.State == ShipFitDetectionState.ScopeMissing)
        {
            Flying = FlyingState.NotShared;
            return;
        }

        if (reading.State != ShipFitDetectionState.Observed || reading.ShipTypeId is null)
        {
            Flying = FlyingState.Unknown;
            return;
        }

        Flying = FlyingState.Ship;
        HullText = hullName;
        FitText = reading.SelectedFit?.Name ?? "no fit recognised";
        FitTooltip = "Fit detection: " + _MatchText(reading.MatchReason);
    }

    internal void ShowQueue(SkillQueueStanding? queue)
    {
        _queue = queue;
        _hasQueueRead = true;
        OnPropertyChanged(nameof(IsQueuePaused));
        OnPropertyChanged(nameof(Queue));
        _ShowTraining(DateTimeOffset.UtcNow);
    }

    /// <summary>Time left moves with the clock and nothing else: a text update, never a read.</summary>
    internal void Tick(DateTimeOffset now) => _ShowTraining(now);

    internal void ShowIsk(decimal? today, decimal? month)
    {
        (HasIskToday, IskTodayText) = _IskText(today);
        (HasIskMonth, IskMonthText) = _IskText(month);
    }

    private static (bool HasIsk, string Text) _IskText(decimal? isk) =>
        isk is { } value && value != 0 ? (true, IskFormat.Compact(value)) : (false, "—");

    [RelayCommand]
    private void Allow(string scope) => _ = _navigation.AllowScope(CharacterId, scope);

    [RelayCommand]
    private void OpenMetrics() => _navigation.OpenMetrics(Character);

    [RelayCommand]
    private void OpenOverlay() => _navigation.OpenDpsOverlay(Character);

    [RelayCommand]
    private void OpenSettings() => _ = _navigation.OpenCharacterSettings(Character);

    public string WhereNotSharedTooltip =>
        $"{Character.Name} did not share Location ({LocationScopeCatalog.ReadLocation}).\nThe system still appears after the first jump or undock: the game log on this PC needs no scope.\nALLOW… opens the scope picker with it ticked, then EVE's sign-in.";

    public string ShipNotSharedTooltip =>
        $"{Character.Name} did not share Current ship ({LocationScopeCatalog.ReadShipType}).\nThe ship only comes from ESI; nothing on this PC reports it. Fit detection needs it too.\nALLOW… opens the scope picker with it ticked, then EVE's sign-in.";

    public string QueueNotSharedTooltip =>
        $"{Character.Name} did not share Skill queue ({SkillsScopeCatalog.ReadSkillQueue}).\nFit skill checks keep working: they use the separate Skills scope.\nALLOW… opens the scope picker with it ticked, then EVE's sign-in.";

    public string NotOnThisPcTooltip =>
        $"The client probe only sees EVE clients running on this PC. {Character.Name} may be playing on another machine; a location is only shown for clients here.";

    private void _ShowWhere() =>
        Where = !IsOnThisPc ? WhereState.NotOnThisPc
            : SystemName is not null ? WhereState.System
            : SharesLocation ? WhereState.Locating
            : WhereState.NotShared;

    private void _ShowTraining(DateTimeOffset now)
    {
        if (!SharesQueue)
        {
            Training = TrainingState.NotShared;
            return;
        }

        if (!_hasQueueRead)
        {
            Training = TrainingState.Unknown;
            return;
        }

        if (_queue is not { } queue)
        {
            Training = TrainingState.Empty;
            IsQueueShort = false;
            return;
        }

        Training = queue.IsPaused ? TrainingState.Paused : TrainingState.Training;
        SkillText = queue.SkillText;
        TrainingDetailText = queue.DetailText(now);
        IsQueueShort = queue.IsPaused || queue.IsShort(now);
        QueueBarFraction = queue.BarFraction(now);
        TrainingTooltip = queue.QueueEndsAt is { } end
            ? $"{queue.Queued} skills queued · queue ends {end.ToLocalTime():d MMM HH:mm}"
            : $"{queue.Queued} skills queued · paused: training resumes when the character logs in";
    }

    private bool _Shares(string scope) => Character.GrantedScopes.Contains(scope, StringComparer.OrdinalIgnoreCase);

    private static string _MatchText(ShipFitMatchReason? reason) => reason switch
    {
        ShipFitMatchReason.ShipName => "the ship's name matches the fit's name",
        ShipFitMatchReason.OnlyFitForShipType => "the only fit in the library for this hull",
        ShipFitMatchReason.AmbiguousShipType => "several fits for this hull, none named after the ship",
        ShipFitMatchReason.Manual => "picked by hand",
        ShipFitMatchReason.Detached => "unlinked by hand",
        _ => "no fit in the library for this hull"
    };
}

public enum WhereState
{
    NotOnThisPc,
    System,
    Locating,
    NotShared
}

public enum FlyingState
{
    NotOnThisPc,
    Unknown,
    Ship,
    NotShared
}

public enum TrainingState
{
    Unknown,
    Training,
    Paused,
    Empty,
    NotShared
}
