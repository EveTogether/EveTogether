using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Skills.Plans;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Commands;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// SKILL IMPACT… (ET-356): scans every SDE skill against a fit snapshot taken from the fit-detail window, then ranks
/// the skills that move at least one chosen stat by combined gain per hour of training. The scan is the expensive
/// part (~500 engine calls, run off the UI thread); toggling which stats to optimise for only re-ranks the cached
/// result — nothing is recalculated in the engine.
///
/// ET-357: with at least one stat chosen, three target cards (can fly / optimal ±III / max) and the greedy training
/// curve between them — computed by <see cref="SkillTargetsCalculator"/> off the UI thread, version-stamped like the
/// scan itself so a superseded recompute (a chip toggled again before the first finishes) is discarded on arrival.
/// </summary>
public sealed class SkillImpactViewModel : ViewModelBase, IRefreshableModule
{
    private static readonly HashSet<SkillImpactStat> LowerIsBetter =
        [SkillImpactStat.AlignTime, SkillImpactStat.Signature];

    private readonly SkillImpactScanner _scanner;
    private readonly IFitValidator? _validator;
    private readonly SkillTrainingEstimator? _trainingEstimator;
    private readonly CharacterAttributeSet? _attributes;
    private readonly SkillTargetsCalculator? _targetsCalculator;
    private readonly Func<IReadOnlyList<SkillPlanRowDraft>, string, Task>? _addToPlan;
    private readonly string _planSourceLabel;
    private readonly ISdeNameResolver _names;
    private readonly FitInput _baseInput;
    private readonly IReadOnlyDictionary<int, int> _trainedLevels;
    private readonly IReadOnlyDictionary<SkillImpactStat, string> _labels;
    private int _scanVersion;
    private int _targetsVersion;
    private SkillImpactResult? _result;
    private bool _isLoading;
    private bool _isLoadingTargets;
    private string? _targetsErrorMessage;
    private SkillTargetCardViewModel? _canFlyCard;
    private SkillTargetCardViewModel? _optimalCard;
    private SkillTargetCardViewModel? _maxCard;
    private SkillTargetCurveViewModel? _curve;

    public SkillImpactViewModel(SkillImpactScanner scanner, IFitValidator? validator,
        SkillTrainingEstimator? trainingEstimator, CharacterAttributeSet? attributes, ISdeNameResolver names,
        string moduleId, string characterName, string shipName, FitInput baseInput,
        IReadOnlyDictionary<int, int> trainedLevels, SkillTargetsCalculator? targetsCalculator = null,
        Func<IReadOnlyList<SkillPlanRowDraft>, string, Task>? addToPlan = null)
    {
        _scanner = scanner;
        _validator = validator;
        _trainingEstimator = trainingEstimator;
        _attributes = attributes;
        _targetsCalculator = targetsCalculator;
        _addToPlan = addToPlan;
        _planSourceLabel = shipName;
        _names = names;
        _baseInput = baseInput;
        _trainedLevels = trainedLevels;
        ModuleId = moduleId;
        CharacterName = characterName;
        ShipName = shipName;
        Chips = _BuildChips();
        _labels = Chips.ToDictionary(chip => chip.Stat, chip => chip.Label);
        foreach (var chip in Chips)
            chip.SelectionChanged += _OnStatSelectionChanged;
    }

    public string ModuleId { get; }
    public string CharacterName { get; }
    public string ShipName { get; }
    public IReadOnlyList<SkillImpactStatChipViewModel> Chips { get; }
    public ObservableCollection<SkillImpactRowViewModel> Rows { get; } = [];

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsLoadingTargets
    {
        get => _isLoadingTargets;
        private set => SetProperty(ref _isLoadingTargets, value);
    }

    public string? TargetsErrorMessage
    {
        get => _targetsErrorMessage;
        private set => SetProperty(ref _targetsErrorMessage, value);
    }

    public SkillTargetCardViewModel? CanFlyCard
    {
        get => _canFlyCard;
        private set => SetProperty(ref _canFlyCard, value);
    }

    public SkillTargetCardViewModel? OptimalCard
    {
        get => _optimalCard;
        private set => SetProperty(ref _optimalCard, value);
    }

    public SkillTargetCardViewModel? MaxCard
    {
        get => _maxCard;
        private set => SetProperty(ref _maxCard, value);
    }

    public SkillTargetCurveViewModel? Curve
    {
        get => _curve;
        private set => SetProperty(ref _curve, value);
    }

    /// <summary>Re-runs the scan against the same snapshot this window was opened with (<see cref="IRefreshableModule"/>):
    /// re-opening SKILL IMPACT… for the same character re-selects this tab rather than building a second one, so
    /// without this it would keep showing whatever the first open computed, however stale.</summary>
    public void RefreshModule() => _ = LoadAsync();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var version = ++_scanVersion;
        IsLoading = true;
        var result = await Task.Run(
            () => _scanner.ScanAsync(_baseInput, _trainedLevels, cancellationToken), cancellationToken);
        if (version != _scanVersion)
            return;   // a newer scan started (e.g. the character changed) while this one ran — its result is stale

        _result = result;
        _ApplyAvailability(result);
        _Recompute();
        IsLoading = false;
        await _RecomputeTargetsAsync(cancellationToken);
    }

    // A chip toggle re-ranks the cached scan synchronously (cheap) but the three target cards and the curve depend
    // on the chosen stats too and need fresh engine calls, so they run again off the UI thread — version-stamped so
    // a toggle fired while an older recompute is still running discards that older one on arrival.
    private void _OnStatSelectionChanged()
    {
        _Recompute();
        _ = _RecomputeTargetsAsync(CancellationToken.None);
    }

    private async Task _RecomputeTargetsAsync(CancellationToken cancellationToken)
    {
        var selected = Chips.Where(chip => chip.IsSelected && chip.IsAvailable).Select(chip => chip.Stat).ToList();
        var scan = _result;
        if (_targetsCalculator is not { } calculator || scan is null || selected.Count == 0)
        {
            ++_targetsVersion;   // discard any recompute already in flight for a since-cleared selection
            CanFlyCard = OptimalCard = MaxCard = null;
            Curve = null;
            TargetsErrorMessage = null;
            return;
        }

        int version = ++_targetsVersion;
        IsLoadingTargets = true;
        SkillTargetsResult result;
        try
        {
            result = await Task.Run(
                () => calculator.CalculateAsync(_baseInput, _trainedLevels, scan, selected, cancellationToken),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (version == _targetsVersion)
            {
                TargetsErrorMessage = "The skill targets could not be computed.";
                IsLoadingTargets = false;
            }
            return;
        }

        if (version != _targetsVersion)
            return;   // a newer selection (or a fresh scan) superseded this recompute — its result is stale

        TargetsErrorMessage = null;
        CanFlyCard = _BuildCard(result.CanFly);
        OptimalCard = _BuildCard(result.Optimal);
        MaxCard = _BuildCard(result.Max);
        Curve = new SkillTargetCurveViewModel(result.Curve);
        IsLoadingTargets = false;
    }

    private SkillTargetCardViewModel _BuildCard(SkillTargetGoal goal)
    {
        Func<Task>? addToPlan = _addToPlan is not { } add ? null : () => add(
            goal.Levels.Select(level => new SkillPlanRowDraft(level.SkillTypeId, level.Level, _planSourceLabel)).ToList(),
            _planSourceLabel);
        return new SkillTargetCardViewModel(goal, _labels, DateTimeOffset.UtcNow, addToPlan);
    }

    private void _ApplyAvailability(SkillImpactResult result)
    {
        foreach (var chip in Chips)
        {
            if (!result.BaseValues.ContainsKey(chip.Stat))
                chip.SetUnavailable("no turret or drone weapons");
            else if (result.Entries.Any(entry => entry.AtFive.ContainsKey(chip.Stat)))
                chip.SetAvailable();
            else if (result.StatsWithOnlyMaxedMovers.Contains(chip.Stat))
                chip.SetUnavailable("all skills that change this are at V");
            else
                chip.SetUnavailable("no skill changes this for this fit");
        }
    }

    private void _Recompute()
    {
        Rows.Clear();
        if (_result is null)
            return;

        var selected = Chips.Where(chip => chip.IsSelected && chip.IsAvailable).Select(chip => chip.Stat).ToList();
        if (selected.Count == 0)
            return;

        var best = new Dictionary<SkillImpactStat, double>();
        foreach (var stat in selected)
        {
            var atFiveValues = _result.Entries
                .Where(entry => entry.AtFive.ContainsKey(stat)).Select(entry => entry.AtFive[stat]).ToList();
            if (atFiveValues.Count > 0)
                best[stat] = LowerIsBetter.Contains(stat) ? atFiveValues.Min() : atFiveValues.Max();
        }

        var rows = new List<SkillImpactRowViewModel>();
        foreach (var entry in _result.Entries)
        {
            var movedSelected = selected.Where(entry.AtFive.ContainsKey).ToList();
            if (movedSelected.Count == 0)
                continue;

            var score = movedSelected.Average(stat => StatShare.Compute(
                _result.BaseValues[stat], entry.AtFive[stat], best[stat], LowerIsBetter.Contains(stat)));
            var trainingTime = _TimeToFive(entry.SkillTypeId);
            var scorePerHour = trainingTime.TotalHours > 0 ? score / trainingTime.TotalHours : score;

            var gains = movedSelected.Select(stat => new SkillImpactStatGain(stat, _labels[stat],
                entry.AtNextLevel.GetValueOrDefault(stat, _result.BaseValues[stat]), entry.AtFive[stat])).ToList();

            string skillName = _names.TypeName(entry.SkillTypeId);
            rows.Add(new SkillImpactRowViewModel(entry.SkillTypeId, skillName, entry.CurrentLevel, gains, trainingTime,
                scorePerHour, _RowAddToPlan(entry.SkillTypeId, skillName)));
        }

        foreach (var row in rows.OrderByDescending(row => row.ScorePerHour))
            Rows.Add(row);
    }

    // ET-357 D1: the "+" per impact-row — adds this one skill to V (prerequisites included) via AddSkillPlanRowsCommand,
    // Source Fit, same write SkillPlanRowFactory already builds for + SKILL/FROM FIT/FROM ITEM (ET-355).
    private Func<Task>? _RowAddToPlan(int skillTypeId, string skillName)
    {
        if (_addToPlan is not { } add || _validator is not { } validator)
            return null;
        return () => add(SkillPlanRowFactory.FromSkill(validator, skillTypeId, 5, _trainedLevels, skillName).Rows, _planSourceLabel);
    }

    // Prerequisites included: the same recursive closure the "Skills Required" panel runs, seeded with just this one
    // candidate skill at V instead of a whole fit — ET-356 point 5's reason for making SkillRequirements public.
    private TimeSpan _TimeToFive(int skillTypeId)
    {
        if (_validator is null || _trainingEstimator is null || _attributes is null)
            return TimeSpan.Zero;

        var gaps = _validator.SkillRequirements([], [new SkillMinimum(skillTypeId, 5)], _trainedLevels);
        var total = TimeSpan.Zero;
        foreach (var gap in gaps)
            total += _trainingEstimator.Estimate(gap.SkillTypeId, gap.CurrentLevel, gap.RequiredLevel, _attributes).TrainingTime;
        return total;
    }

    private static IReadOnlyList<SkillImpactStatChipViewModel> _BuildChips() =>
    [
        new(SkillImpactStat.Dps, "OFFENSE", "DPS"),
        new(SkillImpactStat.DroneDps, "OFFENSE", "Drone DPS"),
        new(SkillImpactStat.Optimal, "OFFENSE", "Optimal"),
        new(SkillImpactStat.Falloff, "OFFENSE", "Falloff"),
        new(SkillImpactStat.Tracking, "OFFENSE", "Tracking"),
        new(SkillImpactStat.Ehp, "TANK", "EHP"),
        new(SkillImpactStat.Capacitor, "CAPACITOR", "Cap"),
        new(SkillImpactStat.Speed, "NAVIGATION", "Speed"),
        new(SkillImpactStat.AlignTime, "NAVIGATION", "Align time"),
        new(SkillImpactStat.Signature, "NAVIGATION", "Signature"),
        new(SkillImpactStat.LockRange, "TARGETING", "Lock range"),
        new(SkillImpactStat.ScanResolution, "TARGETING", "Scan resolution"),
        new(SkillImpactStat.SensorStrength, "TARGETING", "Sensor strength"),
        new(SkillImpactStat.FreeCpu, "FITTING", "Free CPU"),
        new(SkillImpactStat.FreePg, "FITTING", "Free PG"),
    ];
}
