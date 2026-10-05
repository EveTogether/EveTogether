using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>A plan OPTIMISE can be pointed at: its name and its rows, read by the window alongside the snapshot.</summary>
public sealed record OptimisePlanInput(string Name, IReadOnlyList<SkillPlanRow> Rows);

/// <summary>One OPTIMISE FOR choice: the training queue, or one of the character's plans.</summary>
public sealed partial class OptimiseTargetViewModel(string label, int index) : ObservableObject
{
    public string Label { get; } = label;
    public int Index { get; } = index;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// OPTIMISE tab (ET-354, laid out as mockup v5 screen d): for the queue or a plan, the attribute remap that trains it
/// fastest as a now/remap-to table with the time before and after, the implants per slot with what a +4/+5 set would
/// do, and where the time goes per attribute pair. Every figure comes from <see cref="AttributeRemapOptimizer"/>,
/// <see cref="ImplantSetBonus"/> and <see cref="RemapAvailability"/>; this view-model only arranges them.
/// </summary>
public sealed partial class SkillsOptimiseViewModel : ObservableObject
{
    private const int Plus4SetBonus = 4;
    private const int Plus5SetBonus = 5;
    private const double SmallGainShare = 0.05;

    private static readonly (int AttributeId, string Name, string Short)[] _Attributes =
    [
        (DogmaAttributeIds.Charisma, "Charisma", "CHA"),
        (DogmaAttributeIds.Intelligence, "Intelligence", "INT"),
        (DogmaAttributeIds.Memory, "Memory", "MEM"),
        (DogmaAttributeIds.Perception, "Perception", "PER"),
        (DogmaAttributeIds.Willpower, "Willpower", "WIL"),
    ];

    // In-game slot order of the five attribute enhancers.
    private static readonly int[] _SlotAttributeIds =
    [
        DogmaAttributeIds.Perception, DogmaAttributeIds.Memory, DogmaAttributeIds.Willpower,
        DogmaAttributeIds.Intelligence, DogmaAttributeIds.Charisma,
    ];

    private readonly List<_Subject> _subjects = [];
    private readonly CharacterAttributeSet _effective = CharacterAttributeSet.FittingPanelBaseline;
    private readonly CharacterAttributeSet _base = CharacterAttributeSet.FittingPanelBaseline;
    private readonly CharacterAttributeSet _implantBonus = CharacterAttributeSet.FittingPanelBaseline;
    private readonly DateTimeOffset _now;

    public ObservableCollection<OptimiseTargetViewModel> Targets { get; } = [];
    public ObservableCollection<ImplantSlotViewModel> ImplantSlots { get; } = [];
    public ObservableCollection<AttributePairTimeRowViewModel> TimePerAttributePair { get; } = [];
    public IReadOnlyList<string> AttributeHeaders { get; } = _Attributes.Select(a => a.Short).ToList();

    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _emptyText = "No queue to advise on yet — import this character's skills and skill queue first.";
    [ObservableProperty] private OptimiseRemapCardViewModel? _queueCard;
    [ObservableProperty] private OptimiseRemapCardViewModel? _planCard;
    [ObservableProperty] private string _planCardHint = "";
    [ObservableProperty] private string _nextRemapText = "unknown · bonus remaps 0";
    [ObservableProperty] private bool _isRemapAvailable;
    [ObservableProperty] private string _implantsHint = "";
    [ObservableProperty] private string _plus4Label = "queue with a +4 set";
    [ObservableProperty] private string _plus4Text = "—";
    [ObservableProperty] private string _plus4SavingText = "";
    [ObservableProperty] private string _plus5Label = "queue with a +5 set";
    [ObservableProperty] private string _plus5Text = "—";
    [ObservableProperty] private string _plus5SavingText = "";
    [ObservableProperty] private string _implantConclusion = "";
    [ObservableProperty] private string _timeGoesTitle = "WHERE THE QUEUE'S TIME GOES";

    /// <summary>The TRAINING QUEUE REMAP line's text — empty until there is both a queue and imported attributes to
    /// advise on.</summary>
    public string RemapLineText { get; private set; } = "";

    /// <summary>Set by the window: WHAT IF… in the explanation pane jumps to PLANS, where the what-if lives (ET-358).</summary>
    public Action? GoToWhatIf { get; set; }

    public SkillsOptimiseViewModel(SkillsCharacterSnapshot snapshot, IDogmaDataAccessor? dogma, IReadOnlyList<OptimisePlanInput>? plans = null)
    {
        _now = snapshot.Now;
        if (snapshot.Attributes is not { } attributes || dogma is null)
        {
            return; // never imported, or dogma unavailable (design-time preview) — every text keeps its default
        }

        var resolver = new CharacterAttributeResolver(dogma);
        _effective = resolver.Resolve(attributes, snapshot.ImplantTypeIds);
        _base = resolver.Base(attributes, snapshot.ImplantTypeIds);
        _implantBonus = _effective - _base;
        NextRemapText = RemapAvailability.Describe(attributes.AccruedRemapCooldownDate, attributes.BonusRemaps, snapshot.Now);
        IsRemapAvailable = NextRemapText.StartsWith("available", StringComparison.Ordinal);
        _BuildImplantSlots(snapshot, dogma);

        var queueRows = _QueueRows(snapshot);
        var estimator = new SkillTrainingEstimator(dogma);
        _subjects.Add(new _Subject("queue", "training queue", queueRows));
        foreach (var plan in plans ?? [])
        {
            _subjects.Add(new _Subject("plan", plan.Name, plan.Rows.Select(row =>
            {
                var (rank, primary, secondary) = estimator.AttributesOf(row.SkillTypeId);
                return new RemapTrainingRow(primary, secondary,
                    SkillPointMath.SkillPointsForLevel(rank, row.Level) - SkillPointMath.SkillPointsForLevel(rank, row.Level - 1));
            }).ToList()));
        }

        if (queueRows.Count == 0 && _subjects.Count == 1)
        {
            return; // nothing queued and no plan — nothing to advise on
        }

        for (int i = 0; i < _subjects.Count; i++)
        {
            var subject = _subjects[i];
            var label = i == 0 ? $"training queue · {queueRows.Count} levels" : $"plan · {subject.Name}";
            Targets.Add(new OptimiseTargetViewModel(label, i));
        }

        if (queueRows.Count > 0)
        {
            QueueCard = _Card(_subjects[0], "REMAP · TRAINING QUEUE", "queue now");
            var best = AttributeRemapOptimizer.Best(queueRows, _implantBonus);
            var plus4 = _TimeWithSet(queueRows, Plus4SetBonus);
            RemapLineText = $"A remap would save {EveDurationFormatter.Format(_Total(queueRows) - best.TotalTime)} on this queue; " +
                            $"a +4 implant set {EveDurationFormatter.Format(_Total(queueRows) - plus4)}.";
        }

        HasData = true;
        Select(queueRows.Count > 0 ? Targets[0] : Targets.ElementAtOrDefault(1));
    }

    [RelayCommand]
    private void Select(OptimiseTargetViewModel? target)
    {
        if (target is null)
        {
            return;
        }

        foreach (var t in Targets)
        {
            t.IsSelected = t == target;
        }

        var subject = _subjects[target.Index];
        // The right card always shows a plan: the one picked, or the first one when the queue is picked.
        var plan = target.Index > 0 ? subject : _subjects.Skip(1).FirstOrDefault();
        PlanCard = plan is null ? null : _Card(plan, $"REMAP · PLAN {plan.Name.ToUpperInvariant()}, TRAINED FIRST", "plan now");
        PlanCardHint = plan is null ? "No plan yet. Make one on PLANS to see the remap that trains it fastest." : "";
        _ApplyImplantSets(subject);
        _ApplyTimeGoes(subject);
    }

    [RelayCommand]
    private void OpenWhatIf() => GoToWhatIf?.Invoke();

    private OptimiseRemapCardViewModel _Card(_Subject subject, string title, string nowLabel)
    {
        var now = _Total(subject.Rows);
        var best = AttributeRemapOptimizer.Best(subject.Rows, _implantBonus);
        var saved = now - best.TotalTime;
        double share = now > TimeSpan.Zero ? saved / now : 0;
        var nowCells = _Attributes.Select(a => new AttributeCellViewModel(_Number(_base.For(a.AttributeId)), 0)).ToList();
        var remapCells = _Attributes.Select(a =>
        {
            double before = _base.For(a.AttributeId), after = best.BaseAttributes.For(a.AttributeId);
            return new AttributeCellViewModel(_Number(after), after.CompareTo(before));
        }).ToList();

        return new OptimiseRemapCardViewModel(title, nowCells, remapCells, nowLabel,
            $"{EveDurationFormatter.Format(now)} · {_Date(now)}",
            $"{EveDurationFormatter.Format(best.TotalTime)} · {_Date(best.TotalTime)}",
            saved > TimeSpan.Zero ? EveDurationFormatter.Format(saved) : "nothing",
            saved > TimeSpan.Zero ? $"({(share * 100).ToString(share < 0.1 ? "0.0" : "0", CultureInfo.InvariantCulture)}%)" : "",
            share < SmallGainShare,
            _Explain(subject, share));
    }

    // One sentence on why the gain is what it is, from how the time splits over the attribute pairs.
    private string _Explain(_Subject subject, double savedShare)
    {
        var pairs = _Pairs(subject.Rows);
        if (pairs.Count == 0)
        {
            return "";
        }

        string what = subject.Kind == "queue" ? "the queue" : "the plan";
        var top = pairs[0];
        if (savedShare <= 0)
        {
            return $"Today's split already trains {what} fastest; a remap would gain nothing.";
        }

        if (savedShare < SmallGainShare && pairs.Count > 1)
        {
            return $"Small: {what} is split between {top.Name} and {pairs[1].Name}, so no split wins much. " +
                   (subject.Kind == "queue" ? "Keep the remap for a plan that leans one way." : "Keep the remap for the queue.");
        }

        return top.Share >= 0.6
            ? $"{_Capital(what)} is {top.Name} almost throughout. After it, the next yearly remap can go back to suit the rest of the queue."
            : $"{_Capital(what)} leans to {top.Name} ({top.Share * 100:0}% of its time); this split favours it.";
    }

    private void _ApplyImplantSets(_Subject subject)
    {
        string what = subject.Kind == "queue" ? "queue" : "plan";
        var now = _Total(subject.Rows);
        var plus4 = _TimeWithSet(subject.Rows, Plus4SetBonus);
        var plus5 = _TimeWithSet(subject.Rows, Plus5SetBonus);
        var remapSaved = now - AttributeRemapOptimizer.Best(subject.Rows, _implantBonus).TotalTime;
        Plus4Label = $"{what} with a +4 set";
        Plus5Label = $"{what} with a +5 set";
        Plus4Text = EveDurationFormatter.Format(plus4);
        Plus5Text = EveDurationFormatter.Format(plus5);
        Plus4SavingText = now - plus4 > TimeSpan.Zero ? $"−{EveDurationFormatter.Format(now - plus4)}" : "already plugged in";
        Plus5SavingText = now - plus5 > TimeSpan.Zero ? $"−{EveDurationFormatter.Format(now - plus5)}" : "already plugged in";
        var plus4Saved = now - plus4;
        ImplantConclusion = plus4Saved <= TimeSpan.Zero
            ? $"A +4 set adds nothing for this {what}: the slots already hold +4 or better."
            : remapSaved > TimeSpan.Zero
                ? $"For this {what}, a +4 set gains about {Math.Max(1, Math.Round(plus4Saved / remapSaved)):0} times what the remap gains."
                : $"For this {what}, a +4 set gains {EveDurationFormatter.Format(plus4Saved)}; the remap gains nothing.";
    }

    private void _ApplyTimeGoes(_Subject subject)
    {
        TimeGoesTitle = subject.Kind == "queue" ? "WHERE THE QUEUE'S TIME GOES" : "WHERE THE PLAN'S TIME GOES";
        TimePerAttributePair.Clear();
        foreach (var pair in _Pairs(subject.Rows))
        {
            TimePerAttributePair.Add(new AttributePairTimeRowViewModel(pair.Name, pair.Share, EveDurationFormatter.Format(pair.Time)));
        }
    }

    // Time per primary/secondary pair at today's attributes, largest first.
    private List<(string Name, TimeSpan Time, double Share)> _Pairs(IReadOnlyList<RemapTrainingRow> rows)
    {
        var total = _Total(rows);
        return rows.GroupBy(r => (r.PrimaryAttributeId, r.SecondaryAttributeId))
            .Select(g =>
            {
                var time = AttributeRemapOptimizer.TotalTrainingTime(g.ToList(), _effective);
                return ($"{_Name(g.Key.PrimaryAttributeId)} / {_Name(g.Key.SecondaryAttributeId)}", time,
                    total > TimeSpan.Zero ? time / total : 0);
            })
            .OrderByDescending(p => p.time)
            .ToList();
    }

    private void _BuildImplantSlots(SkillsCharacterSnapshot snapshot, IDogmaDataAccessor dogma)
    {
        var reading = ImplantSlotReading.Read(snapshot.ImplantTypeIds, dogma);
        var bySlot = reading.AttributeSlots;
        int hardwirings = reading.Hardwirings;
        for (int slot = 1; slot <= 5; slot++)
        {
            string attribute = _Name(_SlotAttributeIds[slot - 1]);
            if (bySlot.TryGetValue(slot, out var implant))
            {
                string name = snapshot.Sde.TryGetTypeName(implant.TypeId, out var typeName) ? typeName : $"+{implant.Bonus:0}";
                ImplantSlots.Add(new ImplantSlotViewModel($"slot {slot}", name, $"{attribute} +{implant.Bonus:0}", false));
            }
            else
            {
                ImplantSlots.Add(new ImplantSlotViewModel($"slot {slot}", "—", attribute, true));
            }
        }

        string attributeSlots = bySlot.Count == 0 ? "Slots 1–5 are empty"
            : bySlot.Count == 5 ? "Slots 1–5 all hold an attribute implant"
            : $"{bySlot.Count} of slots 1–5 hold an attribute implant";
        ImplantsHint = hardwirings > 0
            ? $"{attributeSlots}; 6–10 hold {hardwirings} hardwiring{(hardwirings == 1 ? "" : "s")}."
            : $"{attributeSlots}; 6–10 are empty.";
    }

    private TimeSpan _Total(IReadOnlyList<RemapTrainingRow> rows) => AttributeRemapOptimizer.TotalTrainingTime(rows, _effective);

    private TimeSpan _TimeWithSet(IReadOnlyList<RemapTrainingRow> rows, int setBonus) =>
        AttributeRemapOptimizer.TotalTrainingTime(rows, _base + ImplantSetBonus.Apply(_implantBonus, setBonus));

    private string _Date(TimeSpan fromNow) =>
        _now.Add(fromNow).ToLocalTime().ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string _Number(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static string _Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static string _Name(int attributeId) =>
        _Attributes.FirstOrDefault(a => a.AttributeId == attributeId).Name ?? "?";

    /// <summary>The TRAINING QUEUE's not-yet-finished rows as remap rows — same level-SP delta TRAINING QUEUE itself
    /// sums for SP IN QUEUE, keyed to the skill's primary/secondary attribute from the SDE.</summary>
    private static IReadOnlyList<RemapTrainingRow> _QueueRows(SkillsCharacterSnapshot snapshot)
    {
        var skillsById = snapshot.Sde.GetGroupsByCategory(16)
            .SelectMany(g => snapshot.Sde.GetSkillsInGroup(g.GroupId))
            .ToDictionary(s => s.TypeId, s => s);

        var rows = new List<RemapTrainingRow>();
        foreach (var entry in snapshot.Queue.Where(e => e.FinishDate is null || e.FinishDate > snapshot.Now))
        {
            if (!skillsById.TryGetValue(entry.SkillTypeId, out var skill))
            {
                continue;
            }

            var remainingSp = SkillPointMath.SkillPointsForLevel(skill.Rank, entry.FinishedLevel)
                             - SkillPointMath.SkillPointsForLevel(skill.Rank, entry.FinishedLevel - 1);
            rows.Add(new RemapTrainingRow(skill.PrimaryAttributeId, skill.SecondaryAttributeId, remainingSp));
        }

        return rows;
    }

    private sealed record _Subject(string Kind, string Name, IReadOnlyList<RemapTrainingRow> Rows);
}
