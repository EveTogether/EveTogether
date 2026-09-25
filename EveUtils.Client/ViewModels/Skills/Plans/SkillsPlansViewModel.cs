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
        await _LoadRowsAsync(cancellationToken);
    }

    partial void OnSelectedPlanChanged(SkillPlan? value) => _ = _LoadRowsAsync(CancellationToken.None);

    partial void OnOrderModeChanged(SkillPlanOrderMode value) => _ = _LoadRowsAsync(CancellationToken.None);

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

    [RelayCommand]
    private async Task AddFromItem()
    {
        if (_validator is null)
        {
            return;
        }

        var text = await _dialogs.PromptTextAsync("Add from an item", "Any published type from the SDE");
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string itemName = text.Trim();
        if (!_snapshot.Sde.TryGetTypeId(itemName, out int typeId))
        {
            StatusMessage = $"No published item named \"{itemName}\".";
            return;
        }

        var result = SkillPlanRowFactory.FromItem(_validator, typeId, _snapshot.Levels, itemName);
        await _AddRowsAsync(SkillPlanRowSource.Item, typeId.ToString(CultureInfo.InvariantCulture), result);
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

        var picker = new FitPickerViewModel(_services, FitPickerMode.Single, alreadyAdded: null, composition: null, currentFitHash: null);
        var fit = await _dialogs.PickFitAsync(picker);
        if (fit is null)
        {
            return;
        }

        EsiFitting? esiFitting;
        try
        {
            esiFitting = JsonSerializer.Deserialize<EsiFitting>(fit.RawJson);
        }
        catch (JsonException)
        {
            esiFitting = null;
        }

        if (esiFitting is null)
        {
            StatusMessage = "This fit could not be read.";
            return;
        }

        var modules = FitInputMapper.BuildModules(esiFitting, _snapshot.Sde, _dogma);
        var baseInput = new FitInput(esiFitting.ShipTypeId, modules, SkillSource.From(_snapshot.Levels), FitInputMapper.BuildDrones(esiFitting));
        var scanner = new SkillImpactScanner(_calculator, _dogma);
        var estimator = new SkillTrainingEstimator(_dogma);
        var targetsCalculator = new SkillTargetsCalculator(_calculator, _validator, estimator, _attributes);

        var viewModel = new SkillImpactViewModel(scanner, _validator, estimator, _attributes,
            FitNameResolverFactory.For(_services), $"skill-impact:plan-fit:{fit.ContentHash}", _characterName, fit.FitName,
            baseInput, _snapshot.Levels, targetsCalculator, (rows, sourceLabel) => _AddPlanRowsAsync(SkillPlanRowSource.Fit, fit.ContentHash, rows, sourceLabel));
        _dialogs.ShowSkillImpact(viewModel);
        await viewModel.LoadAsync();
    }

    // The write both ET-357's per-card ADD TO PLAN and its per-row "+" call into — same command as every other
    // + action on this tab, so a plan edited from the SKILL IMPACT window follows the same reload/message path.
    private async Task _AddPlanRowsAsync(SkillPlanRowSource source, string? sourceRef, IReadOnlyList<SkillPlanRowDraft> rows, string sourceLabel)
    {
        if (rows.Count == 0)
        {
            StatusMessage = $"Nothing to add for {sourceLabel} — every required skill is already trained.";
            return;
        }

        await _AddRowsAsync(source, sourceRef, new SkillPlanBuildResult(rows, null));
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

        var stored = await _reader.GetRowsAsync(plan.Id, CancellationToken.None);
        string text = SkillPlanTextCodec.ToText(stored.Select(row => (row.SkillTypeId, row.Level)).ToList(), _snapshot.Sde);
        await _dialogs.SetClipboardTextAsync(text);
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

        if (built.Message is not null)
        {
            StatusMessage = built.Message;
            return;
        }

        var added = await _dispatcher.Send(new AddSkillPlanRowsCommand(_characterId, plan.Id, source, sourceRef, built.Rows), CancellationToken.None);
        StatusMessage = added.IsSuccess && added.Value > 0 ? null : "Nothing new to add — those levels are already in the plan.";
        await _LoadRowsAsync(CancellationToken.None);
    }

    private async Task _LoadRowsAsync(CancellationToken cancellationToken)
    {
        Rows.Clear();
        if (SelectedPlan is not { } plan || _dogma is null)
        {
            TotalTimeText = "";
            LevelsText = "";
            FlyableAfterText = "";
            WhatIf = null;
            return;
        }

        var stored = await _reader.GetRowsAsync(plan.Id, cancellationToken);
        var timePerSkill = OrderMode == SkillPlanOrderMode.ShortestFirst
            ? SkillPlanTiming.TimePerSkill(new SkillTrainingEstimator(_dogma), stored, _attributes)
            : null;
        var ordered = SkillPlanOrdering.Order(stored, OrderMode, _dogma, timePerSkill);

        var fitRefs = stored.Where(row => row.Source == SkillPlanRowSource.Fit && row.SourceRef is not null)
            .Select(row => (row.SourceRef!, row.SourceLabel ?? "fit")).Distinct().ToList();
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
        for (int i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            var time = SkillPlanTiming.RowTime(estimator, row, _attributes);
            cumulative += time;
            bool queuedNow = _snapshot.Queue.Any(entry => entry.SkillTypeId == row.SkillTypeId && entry.FinishedLevel == row.Level);
            string skillName = _snapshot.Sde.TryGetTypeName(row.SkillTypeId, out var name) ? name : $"Type {row.SkillTypeId}";
            string fromText = row.Source switch
            {
                SkillPlanRowSource.Fit => "FIT",
                SkillPlanRowSource.Item => "ITEM",
                SkillPlanRowSource.Text => "TEXT",
                SkillPlanRowSource.Doctrine => "MIN",
                _ => "SKILL"
            };
            Rows.Add(SkillPlanDisplayRow.ForRow(row, skillName, RomanLevel.Text(row.Level),
                SkillQueueStanding.Until(time), SkillQueueStanding.Until(cumulative), fromText, queuedNow));

            if (milestoneAfter.TryGetValue(i, out var label))
            {
                flyableAfterText = SkillQueueStanding.Until(cumulative);
                Rows.Add(SkillPlanDisplayRow.ForMilestone($"✈ {label} flyable after {flyableAfterText}"));
            }

            if (doctrineFlyableMilestoneAfter.TryGetValue(i, out var doctrineFitLabel))
            {
                Rows.Add(SkillPlanDisplayRow.ForMilestone($"✈ {doctrineFitLabel} flyable after {SkillQueueStanding.Until(cumulative)}"));
            }

            if (doctrineMinimumMilestoneAfter.TryGetValue(i, out var doctrineMinLabel))
            {
                Rows.Add(SkillPlanDisplayRow.ForMilestone($"◆ {doctrineMinLabel} doctrine minimum met after {SkillQueueStanding.Until(cumulative)}"));
            }
        }

        TotalTimeText = ordered.Count == 0 ? "Nothing to train" : $"{SkillQueueStanding.Until(cumulative)} · done {_snapshot.Now.Add(cumulative):ddd d MMM HH:mm}";
        LevelsText = $"{ordered.Count} level{(ordered.Count == 1 ? "" : "s")}";
        FlyableAfterText = flyableAfterText ?? "—";

        WhatIf = ordered.Count == 0 ? null : new SkillsWhatIfViewModel(_services, _dialogs, _snapshot, _characterId, plan.Name, ordered, _dogma);
        if (WhatIf is not null && _validator is not null)
        {
            await WhatIf.LoadOtherCharactersAsync(_validator, _dogma, cancellationToken);
        }
    }
}
