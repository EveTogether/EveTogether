using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Skills;
using EveUtils.Client.ViewModels.Skills.Plans;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Commands;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// SKILL IMPACT… (ET-356): scans every SDE skill against a fit snapshot (~500 engine calls, off the UI thread) and
/// ranks the skills that move a chosen stat by combined gain per hour, re-ranking the cached result when the stat chips
/// change. ET-357 target cards and the training curve come from <see cref="SkillTargetsCalculator"/>, version-stamped
/// so a superseded recompute is discarded.
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
    private Func<IReadOnlyList<SkillPlanRowDraft>, string, Task>? _addToPlan;
    private string _planSourceLabel;
    private readonly ISdeNameResolver _names;
    private FitInput _baseInput;
    private string _shipName;
    private string _statFilter = "";
    private string _paneSubText = "";
    private string _chosenStatsLabel = "";
    private string? _notInListText;
    private string? _footerText;
    private string? _fitWarningText;
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
        _shipName = shipName;
        Chips = SkillImpactStats.All.Select(meta => new SkillImpactStatChipViewModel(meta)).ToList();
        _labels = Chips.ToDictionary(chip => chip.Stat, chip => chip.Label);
        foreach (var chip in Chips)
        {
            chip.SelectionChanged += _OnStatSelectionChanged;
        }

        // The stat menu's two columns, grouped by fit-detail section as in mockup v5's "+ stat" menu.
        SkillImpactStatGroupViewModel Group(string name) => new(name, Chips.Where(chip => chip.Group == name).ToList());
        MenuLeft = [Group("OFFENSE"), Group("TANK"), Group("CAPACITOR")];
        MenuRight = [Group("NAVIGATION"), Group("TARGETING"), Group("FITTING")];
        ChangeFitCommand = new AsyncRelayCommand(_ChangeFitAsync);
        CloseCommand = new RelayCommand(() => Close?.Invoke());
    }

    /// <summary>‹ PLAN: set by SKILLS → PLANS, which shows this view in place of the plan until it is closed.</summary>
    public Action? Close
    {
        get => _close;
        set
        {
            _close = value;
            OnPropertyChanged(nameof(CanClose));
        }
    }

    private Action? _close;

    public bool CanClose => Close is not null;

    public IRelayCommand CloseCommand { get; }

    /// <summary>Gives a screen opened without a plan (the fit detail's SKILL IMPACT…) the PLANS tab's plan write once it
    /// lands there; the cards are rebuilt so ADD TO PLAN appears.</summary>
    public void UseAddToPlan(Func<IReadOnlyList<SkillPlanRowDraft>, string, Task> addToPlan)
    {
        if (_addToPlan is not null)
        {
            return;
        }

        _addToPlan = addToPlan;
        RefreshModule();
    }

    public string ModuleId { get; }
    public string CharacterName { get; }
    public IReadOnlyList<SkillImpactStatChipViewModel> Chips { get; }
    public IReadOnlyList<SkillImpactStatGroupViewModel> MenuLeft { get; }
    public IReadOnlyList<SkillImpactStatGroupViewModel> MenuRight { get; }
    public ObservableCollection<SkillImpactStatChipViewModel> ChosenChips { get; } = [];
    public ObservableCollection<SkillImpactRowViewModel> Rows { get; } = [];

    /// <summary>The fit's name, the FROM A FIT selector's text.</summary>
    public string ShipName
    {
        get => _shipName;
        private set => SetProperty(ref _shipName, value);
    }

    /// <summary>Opens the library fit picker for this character's levels; set by whoever opens the screen, before it
    /// is shown. Null leaves the FROM A FIT selector showing the fit without a way to switch.</summary>
    public Func<IReadOnlyDictionary<int, int>, Task<SkillImpactFit?>>? PickFit { get; set; }

    public bool CanChangeFit => PickFit is not null;

    public IAsyncRelayCommand ChangeFitCommand { get; }

    public string StatFilterWatermark => $"Filter {Chips.Count} stats…";

    public string StatFilter
    {
        get => _statFilter;
        set
        {
            if (SetProperty(ref _statFilter, value))
            {
                foreach (var chip in Chips)
                {
                    chip.IsMatch = string.IsNullOrWhiteSpace(value)
                        || chip.MenuLabel.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase);
                }
                foreach (var group in MenuLeft.Concat(MenuRight))
                {
                    group.Refresh();
                }
            }
        }
    }

    /// <summary>"8 skills · Catbank".</summary>
    public string PaneSubText
    {
        get => _paneSubText;
        private set => SetProperty(ref _paneSubText, value);
    }

    /// <summary>"DPS · ALIGN TIME · CPU FREE", the pane's section label.</summary>
    public string ChosenStatsLabel
    {
        get => _chosenStatsLabel;
        private set => SetProperty(ref _chosenStatsLabel, value);
    }

    public string? NotInListText
    {
        get => _notInListText;
        private set => SetProperty(ref _notInListText, value);
    }

    public string? FooterText
    {
        get => _footerText;
        private set => SetProperty(ref _footerText, value);
    }

    /// <summary>The sentence after "This fit does not fit at any skill level."; null when skills can make it fit.</summary>
    public string? FitWarningText
    {
        get => _fitWarningText;
        private set => SetProperty(ref _fitWarningText, value);
    }

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
    public void RefreshModule() => _ = _ObservedAsync(LoadAsync());

    // RefreshModule and a chip toggle start their work without awaiting it, so any failure past the scan's and the
    // targets' own catches has to land on screen too, never go unobserved.
    private async Task _ObservedAsync(Task work)
    {
        try
        {
            await work;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            IsLoading = false;
            IsLoadingTargets = false;
            TargetsErrorMessage = $"SKILL IMPACT could not be updated: {exception.Message}";
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var version = ++_scanVersion;
        IsLoading = true;
        SkillImpactResult result;
        try
        {
            result = await Task.Run(() => _scanner.ScanAsync(_baseInput, _trainedLevels, cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (version == _scanVersion)
            {
                IsLoading = false;
                TargetsErrorMessage = $"The skill impact scan could not be run: {exception.Message}";
            }

            return;
        }

        if (version != _scanVersion)
        {
            return;   // a newer scan started (e.g. the character changed) while this one ran — its result is stale
        }

        _result = result;
        _ApplyAvailability(result);
        _SelectDefaultStat();
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
        _ = _ObservedAsync(_RecomputeTargetsAsync(CancellationToken.None));
    }

    private async Task _RecomputeTargetsAsync(CancellationToken cancellationToken)
    {
        var chosen = Chips.Where(chip => chip.IsSelected && chip.IsAvailable).ToList();
        var selected = chosen.Select(chip => chip.Stat).ToList();
        var scan = _result;
        var baseInput = _baseInput;
        if (_targetsCalculator is not { } calculator || scan is null || selected.Count == 0)
        {
            ++_targetsVersion;   // discard any recompute already in flight for a since-cleared selection
            CanFlyCard = OptimalCard = MaxCard = null;
            Curve = null;
            FitWarningText = null;
            TargetsErrorMessage = null;
            return;
        }

        int version = ++_targetsVersion;
        IsLoadingTargets = true;
        SkillTargetsResult result;
        try
        {
            result = await Task.Run(
                () => calculator.CalculateAsync(baseInput, _trainedLevels, scan, selected, cancellationToken),
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
        {
            return;   // a newer selection (or a fresh scan) superseded this recompute — its result is stale
        }

        TargetsErrorMessage = null;
        var now = DateTimeOffset.UtcNow;
        CanFlyCard = _BuildCard(result.CanFly, chosen, result, now);
        OptimalCard = _BuildCard(result.Optimal, chosen, result, now);
        MaxCard = _BuildCard(result.Max, chosen, result, now);
        Curve = new SkillTargetCurveViewModel(result.Curve, chosen);
        FitWarningText = _FitWarning(result);
        IsLoadingTargets = false;
    }

    private SkillTargetCardViewModel _BuildCard(SkillTargetGoal goal, IReadOnlyList<SkillImpactStatChipViewModel> chosen,
        SkillTargetsResult result, DateTimeOffset now)
    {
        string source = _planSourceLabel;
        Func<Task>? addToPlan = _addToPlan is not { } add ? null : () => add(
            goal.Levels.Select(level => new SkillPlanRowDraft(level.SkillTypeId, level.Level, source)).ToList(), source);
        return new SkillTargetCardViewModel(goal, chosen, result, _names, now, addToPlan);
    }

    // f5: a resource still short with every fitting skill at V cannot be trained away. The sentence says how short
    // it is now, at can fly and at V, and which short resource can fly's fitting skills do fix.
    private string? _FitWarning(SkillTargetsResult result)
    {
        var unfixable = new List<string>();
        var fixable = new List<string>();
        foreach (var (stat, name) in new[] { (SkillImpactStat.FreePg, "Power grid"), (SkillImpactStat.FreeCpu, "CPU") })
        {
            double now = result.NowValues.GetValueOrDefault(stat);
            double canFly = result.CanFly.Values[stat];
            double atFive = result.FittingAtFive[stat];
            string Short(double value) => value < 0 ? SkillImpactStats.UnsignedAmount(stat, value) : "nothing";
            if (atFive < 0)
            {
                unfixable.Add($"{name} is {Short(now)} short now, {Short(canFly)} at can fly, and still "
                    + $"{Short(atFive)} with every fitting skill at V.");
            }
            else if (now < 0 && result.FittingSkills.Count > 0)
            {
                string skills = string.Join(", ", result.FittingSkills.Select(level =>
                    $"{_names.TypeName(level.SkillTypeId)} {RomanLevel.Text(level.Level)}"));
                fixable.Add($"{name} is fixable: {skills} (part of can fly) takes free {(stat == SkillImpactStat.FreeCpu ? "CPU" : "power grid")} "
                    + $"from {SkillImpactStats.Value(stat, now)} to {SkillImpactStats.Value(stat, canFly)}.");
            }
        }

        return unfixable.Count == 0 ? null
            : string.Join(" ", unfixable.Concat(fixable)) + " Change the fit, or train anyway: the plan still holds for the skills.";
    }

    private async Task _ChangeFitAsync()
    {
        if (PickFit is not { } pick)
        {
            return;
        }

        try
        {
            if (await pick(_trainedLevels) is not { } fit)
            {
                return;
            }

            _baseInput = fit.Input;
            _addToPlan = fit.AddToPlan ?? _addToPlan;
            _planSourceLabel = fit.FitName;
            ShipName = fit.FitName;
            Rows.Clear();
            CanFlyCard = OptimalCard = MaxCard = null;
            Curve = null;
            FitWarningText = null;
            await LoadAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TargetsErrorMessage = $"The fit could not be switched: {exception.Message}";
        }
    }

    // The screen never opens empty: with nothing chosen yet, pick the stat the fit is built for.
    private void _SelectDefaultStat()
    {
        if (Chips.Any(chip => chip.IsSelected))
        {
            return;
        }

        var pick = new[] { SkillImpactStat.Dps, SkillImpactStat.DroneDps, SkillImpactStat.Ehp }
            .Select(stat => Chips.First(chip => chip.Stat == stat))
            .FirstOrDefault(chip => chip.IsAvailable)
            ?? Chips.FirstOrDefault(chip => chip.IsAvailable);
        if (pick is not null)
        {
            pick.IsSelected = true;
        }
    }

    private void _ApplyAvailability(SkillImpactResult result)
    {
        foreach (var chip in Chips)
        {
            if (!result.BaseValues.ContainsKey(chip.Stat))
            {
                chip.SetUnavailable("no turret or drone weapons");
            }
            else if (result.Entries.Any(entry => entry.AtFive.ContainsKey(chip.Stat)))
            {
                chip.SetAvailable();
            }
            else if (result.StatsWithOnlyMaxedMovers.Contains(chip.Stat))
            {
                chip.SetUnavailable("all skills that change this are at V");
            }
            else
            {
                chip.SetUnavailable("no skill changes this for this fit");
            }
        }
    }

    private void _Recompute()
    {
        Rows.Clear();
        var chosenChips = Chips.Where(chip => chip.IsSelected && chip.IsAvailable).ToList();
        ChosenChips.Clear();
        foreach (var chip in chosenChips)
        {
            ChosenChips.Add(chip);
        }
        ChosenStatsLabel = string.Join(" · ", chosenChips.Select(chip => chip.Label.ToUpperInvariant()));
        var left = Chips.Where(chip => chip.IsAvailable && !chip.IsSelected).Select(chip => chip.RuleName).ToList();
        NotInListText = left.Count == 0 ? null
            : $"Skills that only move stats you did not choose ({string.Join(", ", left)}). Add a stat and they come back.";
        PaneSubText = $"0 skills · {CharacterName}";
        FooterText = null;
        if (_result is null)
        {
            return;
        }

        var selected = chosenChips.Select(chip => chip.Stat).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var best = new Dictionary<SkillImpactStat, double>();
        foreach (var stat in selected)
        {
            var atFiveValues = _result.Entries
                .Where(entry => entry.AtFive.ContainsKey(stat)).Select(entry => entry.AtFive[stat]).ToList();
            if (atFiveValues.Count > 0)
            {
                best[stat] = LowerIsBetter.Contains(stat) ? atFiveValues.Min() : atFiveValues.Max();
            }
        }

        var rows = new List<SkillImpactRowViewModel>();
        foreach (var entry in _result.Entries)
        {
            var movedSelected = selected.Where(entry.AtFive.ContainsKey).ToList();
            if (movedSelected.Count == 0)
            {
                continue;
            }

            // Ranked on the next level, the step the row offers: its combined share of what skills can add, per hour.
            var score = movedSelected.Average(stat => StatShare.Compute(_result.BaseValues[stat],
                entry.AtNextLevel.GetValueOrDefault(stat, _result.BaseValues[stat]), best[stat], LowerIsBetter.Contains(stat)));
            var nextTime = _TimeTo(entry.SkillTypeId, entry.CurrentLevel + 1);
            var trainingTime = _TimeTo(entry.SkillTypeId, 5);
            var scorePerHour = nextTime.TotalHours > 0 ? score / nextTime.TotalHours : score;

            var gains = movedSelected.Select(stat => new SkillImpactStatGain(stat, _labels[stat], _result.BaseValues[stat],
                entry.AtNextLevel.GetValueOrDefault(stat, _result.BaseValues[stat]), entry.AtFive[stat])).ToList();

            string skillName = _names.TypeName(entry.SkillTypeId);
            rows.Add(new SkillImpactRowViewModel(entry.SkillTypeId, skillName, entry.CurrentLevel, gains, nextTime, trainingTime,
                scorePerHour, _RowAddToPlan(entry.SkillTypeId, skillName)));
        }

        foreach (var row in rows.OrderByDescending(row => row.ScorePerHour))
        {
            Rows.Add(row);
        }

        PaneSubText = $"{Rows.Count} skill{(Rows.Count == 1 ? "" : "s")} · {CharacterName}";
        if (Rows.FirstOrDefault() is { } top)
        {
            top.IsFirst = true;
            FooterText = $"{top.SkillName} comes first: {top.NextTimeText} for "
                + $"{string.Join(" and ", top.Gains.Select(gain => gain.NextSentenceText))}, "
                + "the most combined gain per hour of training of every skill in the list.";
        }
    }

    // ET-357 D1: the "+" per impact-row — adds this one skill to V (prerequisites included) via AddSkillPlanRowsCommand,
    // Source Fit, same write SkillPlanRowFactory already builds for + SKILL/FROM FIT/FROM ITEM (ET-355).
    private Func<Task>? _RowAddToPlan(int skillTypeId, string skillName)
    {
        if (_addToPlan is not { } add || _validator is not { } validator)
        {
            return null;
        }
        return () => add(SkillPlanRowFactory.FromSkill(validator, skillTypeId, 5, _trainedLevels, skillName).Rows, _planSourceLabel);
    }

    // Prerequisites included: the same recursive closure the "Skills Required" panel runs, seeded with just this one
    // candidate skill at V instead of a whole fit — ET-356 point 5's reason for making SkillRequirements public.
    private TimeSpan _TimeTo(int skillTypeId, int level)
    {
        if (_validator is null || _trainingEstimator is null || _attributes is null)
        {
            return TimeSpan.Zero;
        }

        var gaps = _validator.SkillRequirements([], [new SkillMinimum(skillTypeId, level)], _trainedLevels);
        var total = TimeSpan.Zero;
        foreach (var gap in gaps)
        {
            total += _trainingEstimator.Estimate(gap.SkillTypeId, gap.CurrentLevel, gap.RequiredLevel, _attributes).TrainingTime;
        }
        return total;
    }
}
