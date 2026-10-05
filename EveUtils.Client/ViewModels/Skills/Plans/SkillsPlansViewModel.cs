using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Skills;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Skills.WhatIf;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Commands;
using EveUtils.Shared.Modules.Skills.Plans.Entities;
using EveUtils.Shared.Modules.Skills.Plans.Enums;
using EveUtils.Shared.Modules.Skills.Plans.Repositories;
using Microsoft.Extensions.DependencyInjection;
using SkillQueueStanding = EveUtils.Client.ViewModels.Home.SkillQueueStanding;

namespace EveUtils.Client.ViewModels.Skills.Plans;

/// <summary>
/// PLANS tab (ET-355): a character's skill plans, built from a skill, a fit or an item — never sent anywhere (D-179).
/// Every write goes through <see cref="IDispatcher"/>; this view-model only reads through <see cref="ISkillPlanReader"/>
/// so <c>WriteRepositoryGuardTests</c> holds it to the same rule as every other screen.
/// </summary>
public sealed partial class SkillsPlansViewModel : ObservableObject
{
    private readonly IDispatcher _dispatcher;
    private readonly ISkillPlanReader _reader;
    private readonly IFitValidator? _validator;
    private readonly IDogmaDataAccessor? _dogma;
    private readonly IDogmaCalculator? _calculator;
    private readonly IDialogService _dialogs;
    private readonly IFleetCompositionReader _compositionReader;
    private readonly IServiceProvider _services;
    private readonly SkillsCharacterSnapshot _snapshot;
    private readonly int _characterId;
    private readonly string _characterName;
    private readonly CharacterAttributeSet _attributes;

    public ObservableCollection<SkillPlan> Plans { get; } = [];
    public ObservableCollection<SkillPlanDisplayRow> Rows { get; } = [];

    [ObservableProperty] private SkillPlan? _selectedPlan;
    [ObservableProperty] private SkillPlanOrderMode _orderMode = SkillPlanOrderMode.FlyFirst;
    [ObservableProperty] private string _totalTimeText = "";
    [ObservableProperty] private string _levelsText = "";
    [ObservableProperty] private string _flyableAfterText = "";
    [ObservableProperty] private string? _statusMessage;

    // Mockup v5 screen c: the summary blocks, the plan count, what was dropped, and the right pane's two states.
    [ObservableProperty] private string _totalDoneText = "";
    [ObservableProperty] private string _levelsQueuedText = "";
    [ObservableProperty] private string _flyableAfterSubText = "";
    [ObservableProperty] private string _plansCountText = "";
    [ObservableProperty] private string _droppedText = "";
    [ObservableProperty] private bool _isItemPaneOpen;
    [ObservableProperty] private string _itemSearchText = "";
    [ObservableProperty] private string _itemHint = "";
    [ObservableProperty] private bool _canAddItem;
    private int? _itemTypeId;

    public ObservableCollection<PlanSourceRow> InThisPlan { get; } = [];

    /// <summary>From a fit (mockup v5 screen f, decision D1): shown in this tab in place of the plan until ‹ PLAN.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFromFit), nameof(HasNoFromFit))]
    private SkillImpactViewModel? _fromFit;

    public bool HasFromFit => FromFit is not null;
    public bool HasNoFromFit => FromFit is null;

    /// <summary>Shows a From a fit screen built elsewhere (the fit detail's SKILL IMPACT…) in this tab: the fit switch,
    /// ‹ PLAN and ADD TO PLAN into the selected plan come from here.</summary>
    public void ShowFromFit(SkillImpactViewModel impact)
    {
        impact.PickFit ??= _PickImpactFitAsync;
        impact.Close = () => FromFit = null;
        impact.UseAddToPlan((rows, label) => _AddPlanRowsAsync(SkillPlanRowSource.Fit, $"fit:{label}", rows, label));
        FromFit = impact;
    }
    public ObservableCollection<PlanItemRequirementRow> ItemRequirements { get; } = [];
    public bool IsFlyFirst => OrderMode == SkillPlanOrderMode.FlyFirst;
    public bool IsShortestFirst => OrderMode == SkillPlanOrderMode.ShortestFirst;
    public bool IsByAttribute => OrderMode == SkillPlanOrderMode.ByAttribute;
    public bool HasDropped => DroppedText.Length > 0;
    public string CharacterName => _characterName;

    public string OrderHint => OrderMode switch
    {
        SkillPlanOrderMode.ShortestFirst => "quick levels first, prerequisites kept",
        SkillPlanOrderMode.ByAttribute => "one attribute pair after the other, for a remap",
        _ => "milestones as early as possible",
    };

    /// <summary>The WHAT IF panel for <see cref="SelectedPlan"/> (ET-358) — null without a plan, a validator or
    /// dogma access (design-time preview, or a character whose SDE dependencies never loaded).</summary>
    [ObservableProperty] private SkillsWhatIfViewModel? _whatIf;

    public SkillsPlansViewModel(IServiceProvider services, SkillsCharacterSnapshot snapshot, int characterId, string characterName = "")
    {
        _services = services;
        _dispatcher = services.GetRequiredService<IDispatcher>();
        _reader = services.GetRequiredService<ISkillPlanReader>();
        _validator = services.GetService<IFitValidator>();
        _dogma = services.GetService<IDogmaDataAccessor>();
        _calculator = services.GetService<IDogmaCalculator>();
        _dialogs = services.GetRequiredService<IDialogService>();
        _compositionReader = services.GetRequiredService<IFleetCompositionReader>();
        _snapshot = snapshot;
        _characterId = characterId;
        _characterName = characterName;
        // Base attributes only, no attribute implants folded in — the same simplification SkillsCharacterSnapshot
        // itself makes for CATALOGUE/TRAINING QUEUE's own SP/min figures (SkillAttributeLookup reads base values).
        _attributes = snapshot.Attributes is { } a
            ? new CharacterAttributeSet(a.Charisma, a.Intelligence, a.Memory, a.Perception, a.Willpower)
            : CharacterAttributeSet.FittingPanelBaseline;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Plans.Clear();
        foreach (var plan in await _reader.GetForCharacterAsync(_characterId, cancellationToken))
        {
            Plans.Add(plan);
        }

        SelectedPlan = Plans.FirstOrDefault(p => p.Id == SelectedPlan?.Id) ?? Plans.FirstOrDefault();
        PlansCountText = $"{Plans.Count} plan{(Plans.Count == 1 ? "" : "s")} for {_characterName}";
        await _LoadRowsAsync(cancellationToken);
    }

    partial void OnSelectedPlanChanged(SkillPlan? value) => _ = _LoadRowsObservedAsync();

    partial void OnOrderModeChanged(SkillPlanOrderMode value)
    {
        OnPropertyChanged(nameof(IsFlyFirst));
        OnPropertyChanged(nameof(IsShortestFirst));
        OnPropertyChanged(nameof(IsByAttribute));
        OnPropertyChanged(nameof(OrderHint));
        _ = _LoadRowsObservedAsync();
    }

    partial void OnDroppedTextChanged(string value) => OnPropertyChanged(nameof(HasDropped));

    // "Add from an item" (mockup v5): the item's requirements show as the name is typed, before anything is added.
    partial void OnItemSearchTextChanged(string value)
    {
        ItemRequirements.Clear();
        _itemTypeId = null;
        CanAddItem = false;
        string name = value.Trim();
        if (name.Length == 0 || _validator is null)
        {
            ItemHint = "";
            return;
        }

        if (!_snapshot.Sde.TryGetTypeId(name, out int typeId))
        {
            ItemHint = name.Length > 2 ? $"No published item named \"{name}\"." : "";
            return;
        }

        _itemTypeId = typeId;
        var requirements = _validator.SkillRequirements([typeId], extra: null, new Dictionary<int, int>());
        int missing = 0;
        foreach (var requirement in requirements)
        {
            int trained = _snapshot.LevelOf(requirement.SkillTypeId);
            string skill = _snapshot.Sde.TryGetTypeName(requirement.SkillTypeId, out var skillName) ? skillName : $"Type {requirement.SkillTypeId}";
            bool ok = trained >= requirement.RequiredLevel;
            missing += ok ? 0 : requirement.RequiredLevel - trained;
            ItemRequirements.Add(new PlanItemRequirementRow($"{skill} {RomanLevel.Text(requirement.RequiredLevel)}",
                ok ? $"✓ trained ({RomanLevel.Text(trained)})" : trained == 0 ? "not injected" : $"trained {RomanLevel.Text(trained)}", ok));
        }

        CanAddItem = SelectedPlan is not null;
        ItemHint = requirements.Count == 0 ? "This item needs no skills."
            : missing == 0 ? $"Nothing to add: {_characterName} can already use it."
            : $"{missing} level{(missing == 1 ? "" : "s")} to add, prerequisites included.";
    }

    [RelayCommand]
    private void ToggleItemPane() => IsItemPaneOpen = !IsItemPaneOpen;

    [RelayCommand]
    private async Task AddItem()
    {
        if (_validator is null || _itemTypeId is not { } typeId)
        {
            return;
        }

        string itemName = ItemSearchText.Trim();
        var result = SkillPlanRowFactory.FromItem(_validator, typeId, _snapshot.Levels, itemName);
        await _AddRowsAsync(SkillPlanRowSource.Item, typeId.ToString(CultureInfo.InvariantCulture), result);
        if (result.Rows.Count == 0)
        {
            StatusMessage = null; // the pane already says "Nothing to add"; IN THIS PLAN now lists the item with 0 levels
        }

        OnItemSearchTextChanged(ItemSearchText);
    }

    // PUT PLAN FIRST IN QUEUE…: ET cannot write the queue (no ESI endpoint), so it copies the plan in order as text
    // for the in-game queue, and says so.
    [RelayCommand]
    private async Task PutPlanFirst()
    {
        if (SelectedPlan is null || Rows.Count == 0)
        {
            return;
        }

        await CopyAsText();
        StatusMessage = "Copied the plan in this order as text. Paste it into the in-game skill queue, in front of what is there.";
    }

    [RelayCommand]
    private async Task Share()
    {
        if (WhatIf is { } whatIf)
        {
            await whatIf.ShareCommand.ExecuteAsync(null);
        }
    }

    // The two property hooks above cannot await, so a failed read has to land in StatusMessage, never go unobserved.
    private async Task _LoadRowsObservedAsync()
    {
        try
        {
            await _LoadRowsAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusMessage = $"This plan could not be read: {exception.Message}";
        }
    }

    [RelayCommand]
    private void SetOrderMode(SkillPlanOrderMode mode) => OrderMode = mode;

    [RelayCommand]
    private async Task CreatePlan()
    {
        var name = await _dialogs.PromptTextAsync("New plan", "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var created = await _dispatcher.Send(new CreateSkillPlanCommand(_characterId, name.Trim()), CancellationToken.None);
        if (created.IsSuccess)
        {
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task RenamePlan()
    {
        if (SelectedPlan is not { } plan)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync("Rename plan", "Name", plan.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await _dispatcher.Send(new RenameSkillPlanCommand(_characterId, plan.Id, name.Trim()), CancellationToken.None);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeletePlan()
    {
        if (SelectedPlan is not { } plan)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Delete plan", $"Delete \"{plan.Name}\"? This cannot be undone."))
        {
            return;
        }

        await _dispatcher.Send(new DeleteSkillPlanCommand(_characterId, plan.Id), CancellationToken.None);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task AddSkill()
    {
        if (_validator is null)
        {
            return;
        }

        var text = await _dialogs.PromptTextAsync("Add a skill", "Skill and level, e.g. \"Caldari Cruiser V\"");
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var parsed = SkillPlanTextCodec.Parse(text, _snapshot.Sde);
        if (parsed.Rows.Count == 0)
        {
            StatusMessage = $"Not recognised: \"{text}\".";
            return;
        }

        var draft = parsed.Rows[0];
        string skillName = _snapshot.Sde.TryGetTypeName(draft.SkillTypeId, out var name) ? name : text.Trim();
        var result = SkillPlanRowFactory.FromSkill(_validator, draft.SkillTypeId, draft.Level, _snapshot.Levels, skillName);
        await _AddRowsAsync(SkillPlanRowSource.Skill, null, result);
    }


    // ET-357 D1: + FROM FIT opens a fit picker, then the fit's own SKILL IMPACT window — can fly / optimal ±III / max,
    // with a curve — instead of adding the raw prerequisite closure straight to the plan. ADD TO PLAN there (and the
    // "+" per impact row) write through the same AddSkillPlanRowsCommand this tab's other actions use, Source Fit.
    [RelayCommand]
    private async Task AddFromFit()
    {
        if (_validator is null || _dogma is null || _calculator is null)
        {
            return;
        }

        SkillImpactFit? fit;
        try
        {
            fit = await _PickImpactFitAsync(_snapshot.Levels);
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
            return;
        }

        if (fit is null)
        {
            return;
        }

        var scanner = new SkillImpactScanner(_calculator, _dogma);
        var estimator = new SkillTrainingEstimator(_dogma);
        var targetsCalculator = new SkillTargetsCalculator(_calculator, _validator, estimator, _attributes);

        var viewModel = new SkillImpactViewModel(scanner, _validator, estimator, _attributes,
            FitNameResolverFactory.For(_services), $"skill-impact:plan-fit:{_characterId}:{fit.FitName}", _characterName, fit.FitName,
            fit.Input, _snapshot.Levels, targetsCalculator, fit.AddToPlan)
        {
            PickFit = _PickImpactFitAsync,   // FROM A FIT: switching the fit keeps this character and this plan
        };
        viewModel.Close = () => FromFit = null;
        FromFit = viewModel;
        await viewModel.LoadAsync();
    }

    private Task<SkillImpactFit?> _PickImpactFitAsync(IReadOnlyDictionary<int, int> levels) =>
        SkillImpactFitPicker.PickAsync(_services, _dialogs, levels, fit =>
            (rows, sourceLabel) => _AddPlanRowsAsync(SkillPlanRowSource.Fit, fit.ContentHash, rows, sourceLabel));

    // The write both ET-357's per-card ADD TO PLAN and its per-row "+" call into — same command as every other
    // + action on this tab, so a plan edited from the SKILL IMPACT window follows the same reload/message path.
    private async Task _AddPlanRowsAsync(SkillPlanRowSource source, string? sourceRef, IReadOnlyList<SkillPlanRowDraft> rows, string sourceLabel)
    {
        if (rows.Count == 0)
        {
            StatusMessage = $"Nothing to add for {sourceLabel} — every required skill is already trained.";
            return;
        }

        await _AddRowsAsync(source, sourceRef, new SkillPlanBuildResult(rows, null, sourceLabel));
    }

    [RelayCommand]
    private async Task AddFromDoctrine()
    {
        if (_validator is null)
        {
            return;
        }

        var picker = await DoctrinePickerViewModel.CreateAsync(_compositionReader);
        var picked = await _dialogs.PickDoctrineEntryAsync(picker);
        if (picked is null)
        {
            return;
        }

        var (seedTypeIds, error) = _SeedTypeIdsFromRawJson(picked.Entry.Fit.RawJson);
        if (seedTypeIds is null)
        {
            StatusMessage = error;
            return;
        }

        var skillMinimums = picked.Entry.SkillMinimums.Select(m => new SkillMinimum(m.SkillTypeId, m.Level)).ToList();
        string label = $"{picked.CompositionName} · {picked.RoleName} · {picked.Entry.Fit.FitName}";
        var result = SkillPlanRowFactory.FromDoctrine(_validator, seedTypeIds, skillMinimums, _snapshot.Levels, label);
        await _AddRowsAsync(SkillPlanRowSource.Doctrine, picked.Entry.Id.ToString(CultureInfo.InvariantCulture), result);
    }

    /// <summary>Shared with the doctrine-flyable-milestone lookup in <see cref="_LoadRowsAsync"/> — a fit snapshot's
    /// ship + items, deserialised the same way + FROM FIT already does.</summary>
    private static (List<int>? SeedTypeIds, string? Error) _SeedTypeIdsFromRawJson(string rawJson)
    {
        EsiFitting? esiFitting;
        try
        {
            esiFitting = JsonSerializer.Deserialize<EsiFitting>(rawJson);
        }
        catch (JsonException)
        {
            esiFitting = null;
        }

        if (esiFitting is null)
        {
            return (null, "This fit could not be read.");
        }

        var seedTypeIds = new List<int> { esiFitting.ShipTypeId };
        seedTypeIds.AddRange(esiFitting.Items.Select(item => item.TypeId));
        return (seedTypeIds, null);
    }

    /// <summary>Every distinct <paramref name="source"/> row's (SourceRef, label) pair, skipping the null SourceRef
    /// a + SKILL add leaves behind — used by the doctrine milestone lookup in <see cref="_LoadRowsAsync"/>.</summary>
    private static IEnumerable<(string SourceRef, string Label)> _RefsWithLabel(
        IReadOnlyList<SkillPlanRow> stored, SkillPlanRowSource source, string defaultLabel)
    {
        foreach (var row in stored)
        {
            if (row.Source == source && row.SourceRef is { } sourceRef)
            {
                yield return (sourceRef, row.SourceLabel ?? defaultLabel);
            }
        }
    }

    [RelayCommand]
    private async Task RemoveRow(SkillPlanDisplayRow? row)
    {
        if (row?.Row is not { } stored || SelectedPlan is not { } plan)
        {
            return;
        }

        await _dispatcher.Send(new RemoveSkillPlanRowCommand(_characterId, plan.Id, stored.SkillTypeId, stored.Level), CancellationToken.None);
        await _LoadRowsAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task CopyAsText()
    {
        if (SelectedPlan is not { } plan)
        {
            return;
        }

        // The order on screen (FLY FIRST, SHORTEST FIRST or BY ATTRIBUTE), the same order SHARE copies.
        var shown = Rows.Where(row => !row.IsMilestone && row.Row is not null)
            .Select(row => (row.Row?.SkillTypeId ?? 0, row.Level)).ToList();
        var pairs = shown.Count > 0
            ? shown
            : (await _reader.GetRowsAsync(plan.Id, CancellationToken.None)).Select(row => (row.SkillTypeId, row.Level)).ToList();
        await _dialogs.SetClipboardTextAsync(SkillPlanTextCodec.ToText(pairs, _snapshot.Sde));
    }

    [RelayCommand]
    private async Task ImportFromText()
    {
        if (SelectedPlan is not { } plan)
        {
            return;
        }

        var text = await _dialogs.ImportSkillPlanTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var parsed = SkillPlanTextCodec.Parse(text, _snapshot.Sde);
        if (parsed.Unrecognized.Count > 0)
        {
            StatusMessage = $"{parsed.Unrecognized.Count} line(s) not recognised: {string.Join("; ", parsed.Unrecognized)}";
        }

        if (parsed.Rows.Count == 0)
        {
            return;
        }

        await _dispatcher.Send(
            new AddSkillPlanRowsCommand(_characterId, plan.Id, SkillPlanRowSource.Text, null, parsed.Rows), CancellationToken.None);
        await _LoadRowsAsync(CancellationToken.None);
    }

    private async Task _AddRowsAsync(SkillPlanRowSource source, string? sourceRef, SkillPlanBuildResult built)
    {
        if (SelectedPlan is not { } plan)
        {
            return;
        }

        // Recorded even when nothing is added, so IN THIS PLAN and "Dropped on purpose" can say so.
        var added = await _dispatcher.Send(new AddSkillPlanRowsCommand(_characterId, plan.Id, source, sourceRef, built.Rows,
            built.Label.Length > 0 ? built.Label : null, built.Dropped), CancellationToken.None);
        StatusMessage = built.Message
            ?? (added.IsSuccess && added.Value > 0 ? null : "Nothing new to add — those levels are already in the plan.");
        await _LoadRowsAsync(CancellationToken.None);
    }

    // IN THIS PLAN and "Dropped on purpose": every recorded source with the levels it brought in; rows added before
    // sources were recorded still show, from their own FROM fields.
    private async Task _LoadSourcesAsync(int planId, IReadOnlyList<SkillPlanRow> stored, CancellationToken cancellationToken)
    {
        var sources = (await _reader.GetSourcesAsync(planId, cancellationToken)).ToList();
        foreach (var legacy in stored.Select(r => (r.Source, r.SourceRef, Label: r.SourceLabel ?? "")).Distinct())
        {
            if (!sources.Any(s => _Matches(s.Source, s.SourceRef, s.Label, legacy.Source, legacy.SourceRef, legacy.Label)))
            {
                sources.Add(new SkillPlanSource { Source = legacy.Source, SourceRef = legacy.SourceRef, Label = legacy.Label });
            }
        }

        InThisPlan.Clear();
        var dropped = new List<string>();
        foreach (var source in sources)
        {
            int levels = stored.Count(r => _Matches(source.Source, source.SourceRef, source.Label, r.Source, r.SourceRef, r.SourceLabel ?? ""));
            string label = source.Label.Length > 0 ? source.Label : "imported text";
            InThisPlan.Add(new PlanSourceRow(_Kind(source.Source), label, $"{levels} level{(levels == 1 ? "" : "s")}"));

            var named = source.DroppedLevels.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split(':'))
                .Where(parts => parts.Length == 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _))
                .Select(parts => (_snapshot.Sde.TryGetTypeName(int.Parse(parts[0], CultureInfo.InvariantCulture), out var n) ? n : parts[0])
                    + " " + RomanLevel.Text(int.Parse(parts[1], CultureInfo.InvariantCulture)))
                .ToList();
            if (levels == 0 && source.Id != 0 && source.DroppedLevels.Length == 0 && _AllTrained(source))
            {
                dropped.Add($"everything of the {label} (already trained)");
            }
            else if (named.Count > 0)
            {
                string why = source.Source == SkillPlanRowSource.Doctrine ? "doctrine minimum, already trained" : "already trained";
                dropped.Add($"{_JoinAnd(named)} ({why})");
            }
        }

        DroppedText = dropped.Count == 0 ? "" : $"Dropped on purpose: {_JoinAnd(dropped)}.";
    }

    private static bool _Matches(SkillPlanRowSource a, string? aRef, string aLabel, SkillPlanRowSource b, string? bRef, string bLabel) =>
        a == b && aRef == bRef && (aRef is not null || aLabel == bLabel);

    // A source that added no level is "dropped" only when every level it needs is trained — not when the plan already
    // held them from another source, or the pilot removed its rows. Only an item can be re-checked from its ref.
    private bool _AllTrained(SkillPlanSource source)
    {
        if (_validator is null || source.Source != SkillPlanRowSource.Item
            || !int.TryParse(source.SourceRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var typeId))
        {
            return false;
        }

        return _validator.SkillRequirements([typeId], extra: null, _snapshot.Levels).Count == 0;
    }

    private static string _Kind(SkillPlanRowSource source) => source switch
    {
        SkillPlanRowSource.Fit => "FIT",
        SkillPlanRowSource.Item => "ITEM",
        SkillPlanRowSource.Text => "TEXT",
        SkillPlanRowSource.Doctrine => "MIN",
        _ => "SKILL",
    };

    private static string _JoinAnd(IReadOnlyList<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";

    private static string _Short(int attributeId) => attributeId switch
    {
        DogmaAttributeIds.Charisma => "CHA",
        DogmaAttributeIds.Intelligence => "INT",
        DogmaAttributeIds.Memory => "MEM",
        DogmaAttributeIds.Perception => "PER",
        DogmaAttributeIds.Willpower => "WIL",
        _ => "?",
    };

    private Dictionary<int, string> _GroupNames()
    {
        var names = new Dictionary<int, string>();
        foreach (var group in _snapshot.Sde.GetGroupsByCategory(16))
        {
            foreach (var skill in _snapshot.Sde.GetSkillsInGroup(group.GroupId))
            {
                names[skill.TypeId] = group.Name;
            }
        }

        return names;
    }

    private async Task _LoadRowsAsync(CancellationToken cancellationToken)
    {
        Rows.Clear();
        if (SelectedPlan is not { } plan || _dogma is null)
        {
            TotalTimeText = "";
            LevelsText = "";
            FlyableAfterText = "";
            TotalDoneText = "";
            LevelsQueuedText = "";
            FlyableAfterSubText = "";
            DroppedText = "";
            InThisPlan.Clear();
            WhatIf = null;
            return;
        }

        var stored = await _reader.GetRowsAsync(plan.Id, cancellationToken);
        var timePerSkill = OrderMode == SkillPlanOrderMode.ShortestFirst
            ? SkillPlanTiming.TimePerSkill(new SkillTrainingEstimator(_dogma), stored, _attributes)
            : null;
        var ordered = SkillPlanOrdering.Order(stored, OrderMode, _dogma, timePerSkill);

        var fitRefs = stored.Where(row => row.Source == SkillPlanRowSource.Fit && row.SourceRef is not null)
            .Select(row => (row.SourceRef ?? string.Empty, row.SourceLabel ?? "fit")).Distinct().ToList();
        var milestoneAfter = new Dictionary<int, string>();
        foreach (var (sourceRef, label) in fitRefs)
        {
            if (SkillPlanOrdering.FlyableMilestoneIndex(ordered, sourceRef) is { } index)
            {
                milestoneAfter[index] = label;
            }
        }

        var doctrineRefs = _RefsWithLabel(stored, SkillPlanRowSource.Doctrine, "doctrine entry").Distinct().ToList();
        var doctrineMinimumMilestoneAfter = new Dictionary<int, string>();
        var doctrineFlyableMilestoneAfter = new Dictionary<int, string>();
        foreach (var (sourceRef, label) in doctrineRefs)
        {
            if (SkillPlanOrdering.DoctrineMinimumMilestoneIndex(ordered, sourceRef) is { } minIndex)
            {
                doctrineMinimumMilestoneAfter[minIndex] = label;
            }

            // The fit-required boundary needs the entry's current fit, re-read live (SourceRef is only the entry id) —
            // a deleted entry just leaves this half of the pair off rather than failing the whole load.
            if (_validator is not null && long.TryParse(sourceRef, out var entryId)
                && await _compositionReader.GetEntryAsync(entryId, cancellationToken) is { } entry)
            {
                var (fitSeedTypeIds, _) = _SeedTypeIdsFromRawJson(entry.Fit.RawJson);
                if (fitSeedTypeIds is not null
                    && SkillPlanOrdering.DoctrineFlyableMilestoneIndex(ordered, sourceRef,
                        SkillPlanRowFactory.RequiredLevelPairs(_validator, fitSeedTypeIds, _snapshot.Levels)) is { } flyIndex)
                {
                    doctrineFlyableMilestoneAfter[flyIndex] = label;
                }
            }
        }

        var estimator = new SkillTrainingEstimator(_dogma);
        var cumulative = TimeSpan.Zero;
        string? flyableAfterText = null;
        var visibleQueue = _snapshot.Queue.Where(e => e.FinishDate is null || e.FinishDate > _snapshot.Now)
            .OrderBy(e => e.QueuePosition).ToList();
        var groupNames = _GroupNames();
        int queuedCount = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            var time = SkillPlanTiming.RowTime(estimator, row, _attributes);
            cumulative += time;
            int queuePosition = visibleQueue.FindIndex(entry => entry.SkillTypeId == row.SkillTypeId && entry.FinishedLevel == row.Level) + 1;
            bool queuedNow = queuePosition > 0;
            queuedCount += queuedNow ? 1 : 0;
            string skillName = _snapshot.Sde.TryGetTypeName(row.SkillTypeId, out var name) ? name : $"Type {row.SkillTypeId}";
            var (rank, primary, secondary) = estimator.AttributesOf(row.SkillTypeId);
            string fromText = row.Source switch
            {
                SkillPlanRowSource.Fit => "FIT",
                SkillPlanRowSource.Item => "ITEM",
                SkillPlanRowSource.Text => "TEXT",
                SkillPlanRowSource.Doctrine => "MIN",
                _ => "SKILL"
            };
            Rows.Add(SkillPlanDisplayRow.ForRow(row, skillName, RomanLevel.Text(row.Level),
                SkillQueueStanding.Until(time), SkillQueueStanding.Until(cumulative), fromText, queuedNow) with
            {
                Number = i + 1,
                CurrentLevel = _snapshot.LevelOf(row.SkillTypeId),
                GroupName = groupNames.GetValueOrDefault(row.SkillTypeId, ""),
                RankAttributesText = $"×{rank} · {_Short(primary)}/{_Short(secondary)}",
                QueueChipText = queuedNow ? $"QUEUE #{queuePosition}" : "",
            });

            string when = $"after {SkillQueueStanding.Until(cumulative)} · {SkillsQueueViewModel.When(_snapshot.Now + cumulative, _snapshot.Now)}";
            if (milestoneAfter.TryGetValue(i, out var label))
            {
                flyableAfterText ??= SkillQueueStanding.Until(cumulative);
                Rows.Add(SkillPlanDisplayRow.ForMilestone($"✈  {label} flyable", when));
            }

            if (doctrineFlyableMilestoneAfter.TryGetValue(i, out var doctrineFitLabel))
            {
                flyableAfterText ??= SkillQueueStanding.Until(cumulative);
                Rows.Add(SkillPlanDisplayRow.ForMilestone($"✈  {doctrineFitLabel} flyable", when));
            }

            if (doctrineMinimumMilestoneAfter.TryGetValue(i, out var doctrineMinLabel))
            {
                Rows.Add(SkillPlanDisplayRow.ForMilestone($"◆  {doctrineMinLabel} minimum met", when));
            }
        }

        TotalTimeText = ordered.Count == 0 ? "—" : SkillQueueStanding.Until(cumulative);
        TotalDoneText = ordered.Count == 0 ? "nothing to train" : $"done {SkillsQueueViewModel.When(_snapshot.Now + cumulative, _snapshot.Now)}";
        LevelsText = ordered.Count.ToString(CultureInfo.InvariantCulture);
        LevelsQueuedText = $"{queuedCount} already in the queue";
        FlyableAfterText = flyableAfterText ?? "—";
        FlyableAfterSubText = flyableAfterText is null ? "no fit in this plan" : "the fit's own requirements";
        await _LoadSourcesAsync(plan.Id, stored, cancellationToken);

        WhatIf = ordered.Count == 0 ? null : new SkillsWhatIfViewModel(_services, _dialogs, _snapshot, _characterId, plan.Name, ordered, _dogma);
        if (WhatIf is not null && _validator is not null)
        {
            await WhatIf.LoadOtherCharactersAsync(_validator, _dogma, cancellationToken);
        }
    }
}
