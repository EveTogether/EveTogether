using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// The activity window (ET-98): a run you are still flying, rather than a form you fill in once it is over. That is
/// the whole difference from every other tracker, and it is why the clock is the largest thing on it.
///
/// This is phase 1 — the frame. The clock, the envelope, the five sections and the manual weather/tier entry are
/// here; the fleet <c>Min()</c> over re-based anchors is phase 2 and the bounty/location wiring is phase 3. Sections
/// that wait on another ticket carry that in their summary rather than sample data, because a screenshot of invented
/// numbers is indistinguishable from a screenshot of real ones.
/// </summary>
public sealed partial class ActivityWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>Where the manual weather and tier are remembered. Under <c>ui.</c> with the other shell prefs, and
    /// remembered at all because you fly the same tier several runs in a row — which is what turns two clicks a run
    /// into two clicks an evening.</summary>
    public const string WeatherSettingKey = "ui.activity.weather";

    public const string TierSettingKey = "ui.activity.tier";

    /// <summary>Once a second. The readout is a clock, and a clock cannot be read faster than it ticks.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    /// <summary>The seven abyssal tiers, index = the T-number the filament is sold under.</summary>
    public static IReadOnlyList<string> Tiers { get; } =
        ["Tranquil", "Calm", "Agitated", "Fierce", "Raging", "Chaotic", "Cataclysmic"];

    // Amber then red, on the last five and the last two minutes. Both are enough time to leave, which is the only
    // decision the clock exists to inform.
    private static readonly TimeSpan WarningAt = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CriticalAt = TimeSpan.FromMinutes(2);

    private const string NoClock = "--:--";

    private readonly IServiceProvider _services;
    private readonly GamelogClientService? _gamelog;
    private DispatcherTimer? _timer;
    private bool _isManualRun;
    private RunEnemyObservationCollector? _enemyObservations;

    public ActivityWindowViewModel(ActivityKind kind, IServiceProvider services)
    {
        Kind = kind;
        _services = services;
        _gamelog = services.GetService<GamelogClientService>();
        if (_gamelog is not null)
            _gamelog.CombatObserved += _OnCombatObserved;

        WeatherChoices = AbyssalWeather.All
            .Select((weather, index) => new ActivityChoice
            {
                Index = index,
                Label = weather.Name,
                Tooltip = $"{weather.EnvironmentName} — {weather.Bonus}, penalty on {weather.PenaltyTarget}"
            })
            .ToList();

        TierChoices = Tiers
            .Select((tier, index) => new ActivityChoice { Index = index, Label = tier })
            .ToList();

        Sections = [Activity, Fit, Fleet, Bounty, Loot];
        Refresh(DateTime.UtcNow);
    }

    /// <summary>Which of the two runs this window is showing. Fixed for the life of the window: a run does not turn
    /// into the other kind halfway through.</summary>
    public ActivityKind Kind { get; }

    public IReadOnlyList<RunEnemyObservationViewModel> EnemyObservations => _enemyObservations?.Observations ?? [];

    public bool IsAbyssal => Kind == ActivityKind.Abyssal;

    // ── The run, as far as this phase knows it ──────────────────────────────────────────────────────
    // Settable rather than sourced: phase 1 is the frame, and the two later phases feed these from the fleet's
    // re-based anchors and from ESI. Everything below reads honestly when they are still null.

    /// <summary>The envelope START — the earliest moment anyone in the run could be proved to be in it. Solo for
    /// now; phase 2 takes the <c>Min()</c> over the fleet's re-based anchors and counts how many it is based on.</summary>
    [ObservableProperty] private DateTime? _anchorUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockHint))]
    [NotifyPropertyChangedFor(nameof(IsStartButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsStopButtonVisible))]
    [NotifyPropertyChangedFor(nameof(RunOriginText))]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    private ActivityRunState _runState;

    [ObservableProperty] private DateTime? _stoppedAtUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockHint))]
    [NotifyPropertyChangedFor(nameof(FleetStatusText))]
    private int _fleetMemberCount = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockHint))]
    [NotifyPropertyChangedFor(nameof(FleetStatusText))]
    private int _anchoredFleetMemberCount;

    /// <summary>The solar system the run is in. Always null in the abyss — a pocket has no location, and the window
    /// says so rather than leaving the field blank.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationText))]
    private string? _solarSystem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationText))]
    [NotifyPropertyChangedFor(nameof(BountyText))]
    [NotifyPropertyChangedFor(nameof(IsInsideAbyssal))]
    private bool? _insideAbyssal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationText))]
    private string? _locationDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BountyText))]
    private long _bountyIsk;

    /// <summary>What was looted and what was left, as a label. Without it "19 minutes" says nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LootStrategyText))]
    private string? _lootStrategy;

    /// <summary>What the copied signature's own text said it was (ET-100) — the raw scan-window field, not
    /// anything the SDE could enrich it to. Always null in the abyss; a filament carries no signature.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureTypeText))]
    private string? _signatureGroup;

    /// <summary>The signature's name once fully scanned — same field, same source as <see cref="SignatureGroup"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureSiteText))]
    private string? _signatureName;

    /// <summary>What the site catalogue carries under <see cref="SignatureName"/> (ET-80). Empty is the ordinary
    /// case rather than a fault — the match is on the English name only, so a miss cannot prove the site is absent —
    /// and so is more than one, since 218 catalogue names are shared by 613 dungeons. Nothing below ever picks one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureSiteText))]
    [NotifyPropertyChangedFor(nameof(ThreatText))]
    [NotifyPropertyChangedFor(nameof(ShipRestrictionText))]
    private IReadOnlyList<SdeSite> _matchedSites = [];

    // ── The five sections ───────────────────────────────────────────────────────────────────────────

    public ActivitySection Activity { get; } = new() { Title = "ACTIVITY", IsExpanded = true };

    public ActivitySection Fit { get; } = new() { Title = "FIT" };

    public ActivitySection Fleet { get; } = new() { Title = "FLEET" };

    public ActivitySection Bounty { get; } = new() { Title = "BOUNTY" };

    public ActivitySection Loot { get; } = new() { Title = "LOOT" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FitSummary))]
    private string _fitDetectionText = "choose a character to see its fit suggestion";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FitSummary))]
    private string _fitSelectionText = "no fit chosen";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FitSummary))]
    private bool _hasChosenFit;

    [ObservableProperty] private string _fitVelocityText = "no max velocity";

    [ObservableProperty] private string _fitWarpSpeedText = "no warp speed";

    public string FitSummary => HasChosenFit ? FitSelectionText : FitDetectionText;

    /// <summary>All five in window order — what the test walks to prove none of them is ever silent.</summary>
    public IReadOnlyList<ActivitySection> Sections { get; }

    // ── Weather and tier ────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<ActivityChoice> WeatherChoices { get; }

    public IReadOnlyList<ActivityChoice> TierChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Weather))]
    [NotifyPropertyChangedFor(nameof(HasWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(NeedsWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(IsPickerShown))]
    [NotifyPropertyChangedFor(nameof(WeatherEnvironmentText))]
    [NotifyPropertyChangedFor(nameof(WeatherEffectText))]
    private int? _weatherIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(NeedsWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(IsPickerShown))]
    [NotifyPropertyChangedFor(nameof(TierText))]
    [NotifyPropertyChangedFor(nameof(WeatherEffectText))]
    private int? _tierIndex;

    public AbyssalWeather? Weather => WeatherIndex is { } index ? AbyssalWeather.All[index] : null;

    public bool HasWeatherAndTier => WeatherIndex is not null && TierIndex is not null;

    /// <summary>Drives the one chip in the header that asks for something. Only ever true for an abyssal run — a
    /// site has neither.</summary>
    public bool NeedsWeatherAndTier => IsAbyssal && !HasWeatherAndTier;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPickerShown))]
    private bool _isPickerOpen;

    /// <summary>Twelve buttons are worth the room while the question is open and in the way once it is answered, so
    /// the picker folds behind one line as soon as both halves are set.</summary>
    public bool IsPickerShown => !HasWeatherAndTier || IsPickerOpen;

    // ── The clock ───────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _clockLabel = string.Empty;

    [ObservableProperty] private string _clockText = NoClock;

    [ObservableProperty] private bool _isClockWarning;

    [ObservableProperty] private bool _isClockCritical;

    [ObservableProperty] private string _startText = string.Empty;

    [ObservableProperty] private string _endText = string.Empty;

    public string HeaderTitle => IsAbyssal ? "ABYSSAL RUN" : "SITE RUN";

    public bool IsStartButtonVisible => RunState != ActivityRunState.Running || !_isManualRun;

    public bool IsStopButtonVisible => RunState == ActivityRunState.Running;

    public string FleetStatusText => FleetMemberCount > 1
        ? IsAbyssal
            ? $"based on {AnchoredFleetMemberCount} of {FleetMemberCount} members"
            : $"fleet of {FleetMemberCount} members"
        : "solo";

    public string RunOriginText => RunState == ActivityRunState.NotStarted
        ? "not started"
        : _isManualRun ? "manual" : "estimated from fleet";

    public string StartButtonText => RunState == ActivityRunState.Running ? "OVERRIDE START" : "START";

    /// <summary>
    /// What the clock does not say on its face. <c>AbyssalSpace.Describe</c> writes a "+" for this; here it is a
    /// sentence under the figure instead, which is where it ended up after the first round of review.
    /// </summary>
    public string ClockHint => IsAbyssal
        ? $"{FleetStatusText}: the envelope is the earliest anchored run. The clock is a floor — the moment of entry cannot be observed, "
          + "so this is at most what is left."
        : RunState == ActivityRunState.Stopped
            ? "Stopped runs retain their figures; start creates a new run."
            : "Manual start and stop are the only source for a site run.";

    // ── Section bodies ──────────────────────────────────────────────────────────────────────────────

    public bool IsInsideAbyssal => InsideAbyssal ?? IsAbyssal;

    public string LocationText => IsInsideAbyssal
        ? "none — an abyssal pocket has no location"
        : LocationDisplay ?? SolarSystem ?? "not known yet";

    public string BountyText => IsInsideAbyssal
        ? "— no bounty in abyssal space"
        : BountyIsk > 0 ? $"{BountyIsk:N0} ISK — own character" : "no payouts yet — own character";

    public string SignatureTypeText => SignatureGroup ?? "not known yet";

    public string SignatureSiteText => SignatureName is not { } name
        ? "not known yet"
        : MatchedSites.Count switch
        {
            0 => $"{name} — no catalogue entry under this English name",
            1 => name,
            var count => $"{name} — {count} catalogue entries share this name"
        };

    /// <summary>What the run demands of the ship you are in — the one fact here that can turn you away at the gate,
    /// so it is stated before you warp rather than discovered after.</summary>
    public string ShipRestrictionText => MatchedSites.Count == 0
        ? "not known — no catalogue entry under this name"
        : MatchedSites.Select(_ShipRule).Distinct().ToList() is [{ } only]
            ? only
            : "the entries sharing this name disagree on their ship restriction";

    /// <summary>Never says "unrated": only 38 of the catalogue's sites state a rating at all, so an absent one is a
    /// fact about the catalogue and is worded as one.</summary>
    public string ThreatText => MatchedSites.Count == 0
        ? "not known — no catalogue entry under this name"
        : MatchedSites.Select(site => site.DedRating).Distinct().ToList() switch
        {
            [{ } ded] => $"DED {ded} of 10",
            [null] => "no DED rating in the catalogue",
            _ => "the entries sharing this name disagree on their DED rating"
        };

    public string TierText => TierIndex is { } tier
        ? $"{Tiers[tier]} (Tier {tier})"
        : "not set";

    public string WeatherEnvironmentText => Weather?.EnvironmentName ?? "not set";

    public string WeatherEffectText => Weather is { } weather && TierIndex is { } tier
        ? $"{weather.Bonus} · {_PenaltyRange(tier)} {weather.PenaltyTarget}"
        : "not set";

    public string LootStrategyText => LootStrategy ?? "not set";

    /// <summary>The caption over every ISK figure in the loot section. It names its own source on purpose: the
    /// figures are whatever price column happened to be in the EVE loot window at the moment of the copy, and the
    /// window has no way to price anything itself.</summary>
    public string IskLabel => "ISK — price as it stood in the copied clipboard column";

    // ── Lifecycle ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Restore the remembered weather and tier. Separate from the constructor so the window can be built
    /// synchronously and a test can assert the round-trip without racing anything.</summary>
    public async Task LoadAsync()
    {
        using var scope = _services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Query(new GetSettingsQuery());

        WeatherIndex = _Restore(settings.FirstOrDefault(s => s.Key == WeatherSettingKey)?.Value,
            AbyssalWeather.All.Count);
        TierIndex = _Restore(settings.FirstOrDefault(s => s.Key == TierSettingKey)?.Value, Tiers.Count);

        _SyncChoices();
        Refresh(DateTime.UtcNow);
    }

    [RelayCommand]
    private async Task ChooseFitAsync()
    {
        var dialogs = _services.GetRequiredService<IDialogService>();
        IReadOnlyList<Character> characters = await _services.GetRequiredService<ICharacterRegistry>().GetAllAsync();
        var options = characters
            .Select(character => character.EsiCharacterId is { } id
                ? new CharacterPickOption(id, character.Name, "local character", Enabled: true)
                : null)
            .OfType<CharacterPickOption>()
            .ToList();
        int? characterId = await dialogs.PickCharacterAsync("Choose a character's fit", options);
        if (characterId is null)
            return;

        var detection = _services.GetRequiredService<IShipFitDetectionService>();
        ApplyFitDetection(detection.GetReading(characterId.Value));

        var picker = new FitPickerViewModel(_services, FitPickerMode.Single, alreadyAdded: null,
            composition: null, currentFitHash: null, skillCheckCharacterId: characterId);
        FitReferenceInfo? fit = await dialogs.PickFitAsync(picker);
        if (fit is null)
            return;
        if (fit.LocalFittingId is null)
        {
            await dialogs.ShowMessageAsync("Choose a local fit", "Only a local fit can override the automatic suggestion.");
            return;
        }

        var overrideResult = await detection.SetManualFitAsync(characterId.Value, fit.LocalFittingId);
        if (!overrideResult.IsSuccess)
        {
            await dialogs.ShowMessageAsync("Fit selection", overrideResult.Messages.FirstOrDefault()?.Text ?? "Could not save the fit selection.");
            return;
        }

        FitSelectionText = $"chosen fit: {fit.FitName}";
        HasChosenFit = true;
        EsiFitting? esi;
        try { esi = JsonSerializer.Deserialize<EsiFitting>(fit.RawJson); }
        catch (JsonException) { esi = null; }
        FitStats? stats = esi is null ? null : await _services.GetRequiredService<IFitStatsProvider>().ComputeAsync(esi);
        ApplyFitStats(stats, esi is not null);
        Refresh(DateTime.UtcNow);
    }

    /// <summary>Begin ticking. Separate from the constructor so a test drives <see cref="Refresh"/> with a clock it
    /// controls rather than racing a timer.</summary>
    public void Start()
    {
        if (_timer is not null)
            return;

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh(DateTime.UtcNow);
        _timer.Start();
        Refresh(DateTime.UtcNow);
    }

    /// <summary>Work the whole readout out again. Public and clock-driven so the same code runs under a test as
    /// under the timer.</summary>
    public void Refresh(DateTime nowUtc)
    {
        _RefreshClock(nowUtc);
        _RefreshSummaries();
    }

    [RelayCommand]
    private async Task SelectWeatherAsync(int index)
    {
        WeatherIndex = index;
        _AfterChoice();
        await _PersistAsync(WeatherSettingKey, index.ToString(CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private async Task SelectTierAsync(int index)
    {
        TierIndex = index;
        _AfterChoice();
        await _PersistAsync(TierSettingKey, index.ToString(CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private async Task ClearWeatherAndTierAsync()
    {
        WeatherIndex = null;
        TierIndex = null;
        _AfterChoice();
        await _PersistAsync(WeatherSettingKey, string.Empty);
        await _PersistAsync(TierSettingKey, string.Empty);
    }

    /// <summary>Reopen the picker on the run that is already answered — the one line it folded behind.</summary>
    [RelayCommand]
    private void OpenPicker() => IsPickerOpen = true;

    [RelayCommand]
    private void StartRun() => StartManualRun(DateTime.UtcNow);

    [RelayCommand]
    private void StopRun() => StopRun(DateTime.UtcNow);

    public void StartManualRun(DateTime nowUtc)
    {
        AnchorUtc = nowUtc;
        StoppedAtUtc = null;
        // A stopped manual result is final; a later ESI anchor cannot reopen it.
        _isManualRun = true;
        RunState = ActivityRunState.Running;
        OnPropertyChanged(nameof(IsStartButtonVisible));
        OnPropertyChanged(nameof(RunOriginText));
        OnPropertyChanged(nameof(StartButtonText));
        Refresh(nowUtc);
    }

    public void StopRun(DateTime nowUtc)
    {
        if (RunState != ActivityRunState.Running)
            return;

        StoppedAtUtc = nowUtc;
        RunState = ActivityRunState.Stopped;
        Refresh(nowUtc);
    }

    public void ApplyFleetEnvelope(IReadOnlyList<MetricSample> samples, DateTime receivedUtc)
    {
        List<MetricSample> members = samples
            .Where(sample => sample.Kind == MetricKind.Location)
            .GroupBy(sample => sample.CharacterId)
            .Select(group => group.OrderByDescending(sample => sample.UnixMs).First())
            .ToList();

        FleetMemberCount = members.Count;

        if (!IsAbyssal)
        {
            AnchoredFleetMemberCount = 0;
            Refresh(receivedUtc);
            return;
        }

        List<DateTime> anchors = members
            .Select(sample => AbyssalSpace.AnchorFromWire(sample.AbyssalAnchorMs, sample.UnixMs, receivedUtc))
            .OfType<DateTime>()
            .ToList();

        AnchoredFleetMemberCount = anchors.Count;

        if (RunState != ActivityRunState.Stopped && !_isManualRun && anchors.Count > 0)
        {
            AnchorUtc = anchors.Min();
            StoppedAtUtc = null;
            RunState = ActivityRunState.Running;
        }

        Refresh(receivedUtc);
    }

    public void ApplyLocation(EsiLocationReading reading, DpsViewModel character)
    {
        InsideAbyssal = reading.Inside;
        LocationDisplay = character.LocationDisplay;
        ISdeAccessor? sde = _services.GetService<ISdeAccessor>();
        _enemyObservations = sde is null ? null : new RunEnemyObservationCollector(character.CharacterId,
            name => sde.TryGetTypeId(name, out int typeId) ? typeId : null);
        OnPropertyChanged(nameof(EnemyObservations));
        Refresh(DateTime.UtcNow);
    }

    public void AddBounty(BountyEvent bounty)
    {
        if (!IsInsideAbyssal)
            BountyIsk += bounty.Isk;

        Refresh(DateTime.UtcNow);
    }

    public void Dispose()
    {
        if (_gamelog is not null)
            _gamelog.CombatObserved -= _OnCombatObserved;
        _timer?.Stop();
        _timer = null;
    }

    private void _OnCombatObserved(int characterId, string target)
    {
        if (RunState != ActivityRunState.Running)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _enemyObservations?.Record(characterId, target));
    }

    // ── Internals ───────────────────────────────────────────────────────────────────────────────────

    private void _RefreshClock(DateTime nowUtc)
    {
        DateTime effectiveNow = StoppedAtUtc ?? nowUtc;
        ClockLabel = IsAbyssal
            ? RunState == ActivityRunState.Stopped ? "TIME LEFT AT STOP" : "TIME LEFT"
            : "ELAPSED";

        if (AnchorUtc is not { } start)
        {
            ClockText = NoClock;
            IsClockWarning = false;
            IsClockCritical = false;
            StartText = "not started";
            EndText = "not started";
            return;
        }

        StartText = _LocalTime(start);

        if (!IsAbyssal)
        {
            ClockText = _Elapsed(effectiveNow - start);
            IsClockWarning = false;
            IsClockCritical = false;
            EndText = StoppedAtUtc is { } stopped ? _LocalTime(stopped) : "still running";
            return;
        }

        // END is the deadline, not the moment the last pilot got out: at RunLimit the ship and the pod are gone,
        // and that is the only end time worth putting on screen while the run is still going.
        EndText = StoppedAtUtc is { } stoppedAt ? _LocalTime(stoppedAt) : _LocalTime(start + AbyssalSpace.RunLimit);

        // No remaining time is the loudest state there is, not the absence of one: past the deadline we are already
        // wrong about something, and a lifted `null <= CriticalAt` would quietly have shown that in the resting
        // colour.
        var remaining = AbyssalSpace.Remaining(start, effectiveNow);
        ClockText = remaining is { } left ? _Elapsed(left) : NoClock;
        IsClockCritical = remaining is null || remaining <= CriticalAt;
        IsClockWarning = remaining > CriticalAt && remaining <= WarningAt;
    }

    // The signature arrives after construction, from the object initialiser the toast opens the window with — so the
    // shut ACTIVITY header has to be worked out again then, not only on the next clock tick.
    partial void OnSignatureNameChanged(string? value) => _RefreshSummaries();

    partial void OnMatchedSitesChanged(IReadOnlyList<SdeSite> value) => _RefreshSummaries();

    private void _RefreshSummaries()
    {
        Activity.HeaderSummary = _ActivitySummary();
        Fit.HeaderSummary = FitSummary;
        Fleet.HeaderSummary = FleetMemberCount > 1 ? FleetStatusText : "solo — no fleet";
        Bounty.HeaderSummary = BountyText;
        Loot.HeaderSummary = "waiting on ET-65";
    }

    internal void ApplyFitDetection(ShipFitDetectionReading reading)
    {
        FitDetectionText = reading.State switch
        {
            ShipFitDetectionState.Unobserved => "ship type has not been read yet",
            ShipFitDetectionState.ScopeMissing => "ship-type scope is missing",
            ShipFitDetectionState.Observed when reading.MatchReason == ShipFitMatchReason.NoFitFound =>
                "no known fit matches the observed ship",
            ShipFitDetectionState.Observed when reading.SelectedFit is { } fit =>
                $"suggested fit: {fit.Name} ({_FitMatchReason(reading.MatchReason)})",
            ShipFitDetectionState.Observed => "no single fit matches the observed ship",
            _ => "ship fit is unavailable"
        };
        Refresh(DateTime.UtcNow);
    }

    internal void ApplyFitStats(FitStats? stats, bool fitCouldBeRead)
    {
        if (!fitCouldBeRead)
        {
            FitVelocityText = "fit could not be read";
            FitWarpSpeedText = "fit could not be read";
            return;
        }

        FitVelocityText = stats is null ? "no max velocity" : $"max velocity: {stats.MaxVelocity:N0} m/s";
        FitWarpSpeedText = stats is null ? "no warp speed" : $"warp speed: {stats.WarpSpeed:N2} AU/s";
    }

    private static string _FitMatchReason(ShipFitMatchReason? reason) => reason switch
    {
        ShipFitMatchReason.ShipName => "name matches the observed ship",
        ShipFitMatchReason.OnlyFitForShipType => "only known fit for this ship type",
        ShipFitMatchReason.Manual => "manual choice",
        _ => "automatic suggestion"
    };

    private string _ActivitySummary()
    {
        if (!IsAbyssal)
            return string.Join(" · ", new[] { SignatureName ?? "no signature", _ShortDemand(), SolarSystem }
                .Where(part => part is not null));

        return Weather is { } weather && TierIndex is { } tier
            ? $"{Tiers[tier]} T{tier} · {weather.Name} · no location"
            : "not set yet · no location";
    }

    /// <summary>The shut header carries what the run demands, not only what it is called. Silent when the entries
    /// sharing the name do not agree — a demand is worth nothing if it might be the neighbour's.</summary>
    private string? _ShortDemand()
    {
        if (MatchedSites.Count == 0)
            return null;

        if (MatchedSites.Select(site => site.DedRating).Distinct().ToList() is [{ } ded])
            return $"DED {ded}";

        return MatchedSites.All(site => site.IsShipRestricted) ? "ship-restricted" : null;
    }

    /// <summary>A restricted site whose allow-list resolves to no groups is restricted all the same — a handful of
    /// the catalogue's type lists express themselves per hull, and reading that as "anything goes" is the one
    /// mistake here that costs a ship.</summary>
    private static string _ShipRule(SdeSite site) =>
        !site.IsShipRestricted
            ? "no ship restriction in the catalogue"
            : site.AllowedShipGroups is []
                ? "restricted, but the catalogue does not state it as ship groups"
                : string.Join(", ", site.AllowedShipGroups.Select(group => group.Name).Order());

    private void _AfterChoice()
    {
        IsPickerOpen = !HasWeatherAndTier;
        _SyncChoices();
        Refresh(DateTime.UtcNow);
    }

    /// <summary>Mirror the selection onto the buttons. The choices carry it themselves so the picker can be a flat
    /// <c>ItemsControl</c> instead of five and seven hand-written buttons.</summary>
    private void _SyncChoices()
    {
        foreach (var choice in WeatherChoices)
            choice.IsSelected = choice.Index == WeatherIndex;

        foreach (var choice in TierChoices)
            choice.IsSelected = choice.Index == TierIndex;
    }

    private async Task _PersistAsync(string key, string value)
    {
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(new SetSettingCommand(key, value));
    }

    /// <summary>The resist penalty is rolled per site rather than fixed per tier, so the window shows the band it
    /// can land in instead of a number it would be inventing — the same three strengths AbyssalBeacons offers as an
    /// explicit choice.</summary>
    private static string _PenaltyRange(int tier) => tier <= 3 ? "-30% or -50%" : "-50% or -70%";

    /// <summary>A stored index that no longer addresses anything is treated as unset — the alternative is a window
    /// that throws on open because a list got shorter.</summary>
    private static int? _Restore(string? stored, int count) =>
        int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
        && index >= 0 && index < count
            ? index
            : null;

    private static string _LocalTime(DateTime utc) =>
        utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>mm:ss, counting past the hour rather than wrapping — a site run is not bounded by anything.</summary>
    private static string _Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes:00}:{span.Seconds:00}");
    }
}
