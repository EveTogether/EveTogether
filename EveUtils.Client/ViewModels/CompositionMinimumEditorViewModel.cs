using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Repositories;
using Dispatcher = Avalonia.Threading.Dispatcher;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// COMP's "Skill minimum · fit" pane (ET-341 mockup b · minimum editor): one entry's doctrine skill minimums, edited
/// tentatively and saved through the same <see cref="IFleetCompositionClient.EditEntryAsync"/> call the full composition
/// editor makes. The effect on the own characters re-runs <see cref="CompositionReadinessCalculator"/> on the edits.
/// </summary>
public sealed partial class CompositionMinimumEditorViewModel : ObservableObject
{
    private static readonly TimeSpan Within = TimeSpan.FromDays(30);

    private readonly CompositionReadinessEntry _readiness;
    private readonly FleetCompositionEntryInfo _entry;
    private readonly IFleetCompositionClient _client;
    private readonly CompositionReadinessCalculator _calculator;
    private readonly IReadOnlyList<CompositionCharacterSnapshot> _snapshots;
    private readonly IReadOnlyDictionary<string, int> _skillIds;
    private readonly Func<int, string> _typeName;
    private readonly ISkillPlanReader? _plans;
    private readonly Func<Task> _saved;
    private readonly Action _closed;

    public CompositionMinimumEditorViewModel(CompositionReadinessEntry readiness, FleetCompositionEntryInfo entry,
        IFleetCompositionClient client, CompositionReadinessCalculator calculator,
        IReadOnlyList<CompositionCharacterSnapshot> snapshots, IReadOnlyDictionary<string, int> skillIds,
        Func<int, string> typeName, ISkillPlanReader? plans, Func<Task> saved, Action closed)
    {
        _readiness = readiness;
        _entry = entry;
        _client = client;
        _calculator = calculator;
        _snapshots = snapshots;
        _skillIds = skillIds;
        _typeName = typeName;
        _plans = plans;
        _saved = saved;
        _closed = closed;
        Entry = new EditorEntryViewModel(entry.Id, entry.Fit, readiness.HullName, entry.EntryMinCount,
            skillMinimums: entry.SkillMinimums, fitSkillLevels: readiness.FitSkillLevels, skillName: typeName);
        SkillNames = [.. skillIds.Keys.Order(StringComparer.OrdinalIgnoreCase)];
        Entry.SkillMinimums.CollectionChanged += _OnMinimumsChanged;
        foreach (EditorSkillMinimumViewModel row in Entry.SkillMinimums)
        {
            row.PropertyChanged += _OnMinimumChanged;
        }
        _Recompute();
    }

    public EditorEntryViewModel Entry { get; }
    public IReadOnlyList<string> SkillNames { get; }
    public string Title => $"Skill minimum · {_readiness.FitName}";
    public string RoleName => _readiness.RoleName;
    public string CompositionName => _readiness.CompositionName;
    public string FitRequiresText =>
        $"{_readiness.FitRequiresText} A minimum at or below that level adds nothing, and the editor says so.";

    public ObservableCollection<SkillPlanOption> PlanOptions { get; } = [];
    public bool HasPlanOptions => PlanOptions.Count > 0;

    [ObservableProperty] private string _effectLabel = "";
    [ObservableProperty] private string _flyToday = "";
    [ObservableProperty] private string _meetToday = "";
    [ObservableProperty] private string _meetWithin = "";
    [ObservableProperty] private string _soonest = "";
    [ObservableProperty] private string _effectHint = "";
    [ObservableProperty] private string _status = "";

    /// <summary>Every own character's skill plans, for FROM A PLAN….</summary>
    public async Task LoadPlansAsync()
    {
        if (_plans is null)
        {
            return;
        }
        foreach (CompositionCharacterSnapshot snapshot in _snapshots.Where(snapshot => snapshot.CharacterId > 0))
        {
            foreach (var plan in await _plans.GetForCharacterAsync(snapshot.CharacterId))
            {
                PlanOptions.Add(new SkillPlanOption(plan.Id, $"{snapshot.Name} · {plan.Name}"));
            }
        }
        OnPropertyChanged(nameof(HasPlanOptions));
    }

    /// <summary>Adds a plan's rows as minimums: the highest planned level per skill, never lowering one already set.</summary>
    [RelayCommand]
    private async Task FromPlan(SkillPlanOption? option)
    {
        if (option is null || _plans is null)
        {
            return;
        }
        try
        {
            var rows = await _plans.GetRowsAsync(option.PlanId);
            foreach (var skill in rows.GroupBy(row => row.SkillTypeId))
            {
                Entry.AddSkillMinimum(skill.Key, _typeName(skill.Key), skill.Max(row => row.Level));
            }
            Status = rows.Count == 0 ? $"\"{option.Label}\" has no rows." : "";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"This plan could not be read: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        try
        {
            (bool ok, string message) = await _client.EditEntryAsync(_entry.Id, _entry.EntryMinCount, Entry.SkillMinimumList);
            if (!ok)
            {
                Status = message;
                return;
            }
            await _saved();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"The minimum could not be saved: {exception.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => _closed();

    // "+ add a skill…" adds on ADD or Enter only, as the full composition editor does: adding as soon as the text equals
    // a name would add "Mining" while the pilot is still typing "Mining Upgrades".
    [RelayCommand]
    private void AddSkill()
    {
        string name = Entry.NewSkillText.Trim();
        if (!_skillIds.TryGetValue(name, out int skillTypeId))
        {
            Status = name.Length == 0 ? "" : $"No skill named \"{name}\".";
            return;
        }

        Status = "";
        Entry.AddSkillMinimum(skillTypeId, _typeName(skillTypeId), Math.Clamp(Entry.NewSkillLevelIndex + 1, 1, 5));
        Entry.NewSkillText = "";
    }

    private void _OnMinimumsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (EditorSkillMinimumViewModel row in args.NewItems?.OfType<EditorSkillMinimumViewModel>() ?? [])
        {
            row.PropertyChanged += _OnMinimumChanged;
        }
        _Recompute();
    }

    private void _OnMinimumChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EditorSkillMinimumViewModel.Level))
        {
            _Recompute();
        }
    }

    private void _Recompute()
    {
        CompositionReadinessEntry tentative = _calculator.Evaluate(_readiness.RoleName, _entry.Fit, Entry.SkillMinimumList, _snapshots);
        IReadOnlyList<CompositionCharacterReadiness> characters = tentative.VisibleCharacters;
        int count = characters.Count;
        EffectLabel = $"EFFECT ON YOUR {count} CHARACTERS";
        FlyToday = $"{tentative.ReadyCount + tentative.FliesCount} of {count}";
        MeetToday = $"{tentative.ReadyCount} of {count}";
        MeetWithin = $"{characters.Count(character => character.ToMin is { } time && time <= Within)} of {count}";
        Soonest = characters
            .Where(character => character.Status != CompositionReadinessStatus.Ready && character.ToMin is not null)
            .OrderBy(character => character.ToMin)
            .FirstOrDefault() is { ToMin: { } soonest } next
            ? $"{next.Name} · {EveDurationFormatter.Format(soonest)}"
            : "—";
        EffectHint = $"Per character: ALL {count} › on the entry opens the list.";
    }
}

/// <summary>One character's plan in the FROM A PLAN… list.</summary>
public sealed record SkillPlanOption(int PlanId, string Label);
