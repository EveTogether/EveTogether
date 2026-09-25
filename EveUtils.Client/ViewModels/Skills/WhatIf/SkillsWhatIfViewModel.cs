using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Entities;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Skills.WhatIf;

/// <summary>
/// WHAT IF panel (ET-358), hosted in PLANS for the selected plan and character: the five scenarios from
/// <see cref="WhatIfCalculator"/> plus the "your other characters" summary, and the SHARE dialog. Pure display over
/// data <see cref="SkillsPlansViewModel"/> already read — this view-model never dispatches a command and never talks
/// to a repository itself except to look up the pilot's other characters for the summary line.
/// </summary>
public sealed partial class SkillsWhatIfViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IDialogService _dialogs;
    private readonly SkillsCharacterSnapshot _snapshot;
    private readonly int _characterId;
    private readonly string _planName;
    private readonly IReadOnlyList<SkillPlanRow> _rows;

    public IReadOnlyList<WhatIfScenario> Scenarios { get; }

    [ObservableProperty] private string _otherCharactersFlyTodayLine = "";
    [ObservableProperty] private string _otherCharactersSoonestLine = "";

    public SkillsWhatIfViewModel(IServiceProvider services, IDialogService dialogs, SkillsCharacterSnapshot snapshot,
        int characterId, string planName, IReadOnlyList<SkillPlanRow> orderedRows, IDogmaDataAccessor dogma)
    {
        _services = services;
        _dialogs = dialogs;
        _snapshot = snapshot;
        _characterId = characterId;
        _planName = planName;
        _rows = orderedRows;

        // Reuses the snapshot's own clock reading rather than a fresh one, so the scenario dates below never drift
        // from the TOTAL TIME / FLYABLE AFTER lines the same PLANS tab already shows for the same "now".
        var now = snapshot.Now;

        var resolver = new CharacterAttributeResolver(dogma);
        var effective = snapshot.Attributes is { } imported
            ? resolver.Resolve(imported, snapshot.ImplantTypeIds)
            : CharacterAttributeSet.FittingPanelBaseline;
        var baseAttributes = snapshot.Attributes is { } importedForBase
            ? resolver.Base(importedForBase, snapshot.ImplantTypeIds)
            : CharacterAttributeSet.FittingPanelBaseline;
        var implantBonus = effective - baseAttributes;

        Scenarios = WhatIfCalculator.Compute(orderedRows, snapshot.Queue, dogma, effective, implantBonus, now);
    }

    /// <summary>Fills in AC3's "your other characters" summary — a separate step from the constructor's pure
    /// scenario compute, since it reads every other character's stored skills off the SQLite repositories.</summary>
    public async Task LoadOtherCharactersAsync(IFitValidator validator, IDogmaDataAccessor dogma, CancellationToken cancellationToken)
    {
        var registry = _services.GetService<ICharacterRegistry>();
        var skillRepository = _services.GetService<ICharacterSkillRepository>();
        var attributesRepository = _services.GetService<ICharacterAttributesRepository>();
        if (registry is null || skillRepository is null || attributesRepository is null)
        {
            return;
        }

        var resolver = new CharacterAttributeResolver(dogma);
        var others = new List<CompositionCharacterSnapshot>();
        foreach (var character in await registry.GetAllAsync(cancellationToken))
        {
            if (character.EsiCharacterId is not { } otherCharacterId || otherCharacterId == _characterId)
            {
                continue; // "other characters" excludes the one already on screen
            }

            bool hasScope = character.HasScope(SkillsScopeCatalog.ReadSkills);
            var levels = hasScope ? await skillRepository.GetLevelsAsync(otherCharacterId, cancellationToken) : new Dictionary<int, int>();
            var storedAttributes = hasScope ? await attributesRepository.GetAsync(otherCharacterId, cancellationToken) : null;
            others.Add(new CompositionCharacterSnapshot(character.Name, hasScope, HasQueueScope: false, levels,
                storedAttributes is null ? null : resolver.Resolve(storedAttributes, []), Queue: []));
        }

        var summary = OtherCharactersCalculator.Compute(others, _rows, validator, new SkillTrainingEstimator(dogma), _snapshot.Now);
        OtherCharactersFlyTodayLine = summary.FlyItTodayLine;
        OtherCharactersSoonestLine = summary.SoonestLine;
    }

    [RelayCommand]
    private async Task Share()
    {
        Func<Task> putInDoctrine = async () =>
        {
            var client = new LocalFleetCompositionClient(
                _services.GetRequiredService<ClientFleetService>(),
                _services.GetRequiredService<IFleetCompositionReader>(),
                _characterId);
            var minimums = _rows.Select(row => new SkillMinimum(row.SkillTypeId, row.Level)).ToList();
            var editor = CompositionEditorViewModel.ForNew(_services, client, $"Plan: {_planName}", minimums);
            await _dialogs.ShowCompositionEditorAsync(editor);
        };

        var vm = new SkillPlanShareDialogViewModel(_dialogs, _planName, _rows, _snapshot.Sde, putInDoctrine);
        await _dialogs.ShowSkillPlanShareAsync(vm);
    }
}
