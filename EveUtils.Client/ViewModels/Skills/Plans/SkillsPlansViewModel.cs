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
    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly SkillsCharacterSnapshot _snapshot;
    private readonly int _characterId;
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

    public SkillsPlansViewModel(IServiceProvider services, SkillsCharacterSnapshot snapshot, int characterId)
    {
        _services = services;
        _dispatcher = services.GetRequiredService<IDispatcher>();
        _reader = services.GetRequiredService<ISkillPlanReader>();
        _validator = services.GetService<IFitValidator>();
        _dogma = services.GetService<IDogmaDataAccessor>();
        _dialogs = services.GetRequiredService<IDialogService>();
        _snapshot = snapshot;
        _characterId = characterId;
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

    [RelayCommand]
    private async Task AddFromFit()
    {
        if (_validator is null)
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

        var seedTypeIds = new List<int> { esiFitting.ShipTypeId };
        seedTypeIds.AddRange(esiFitting.Items.Select(item => item.TypeId));
        var result = SkillPlanRowFactory.FromFit(_validator, seedTypeIds, _snapshot.Levels, fit.FitName);
        await _AddRowsAsync(SkillPlanRowSource.Fit, fit.ContentHash, result);
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
