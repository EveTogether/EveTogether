using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Skills.WhatIf;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Entities;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-358 A2-A4: the WHAT IF panel's SHARE dialog never writes on its own, "your other characters" stays two lines
/// no matter how many characters there are, and EVE Workbench is shown disabled with "later" (D8).
/// </summary>
public class SkillsWhatIfSharingTests
{
    private const int TargetSkill = 100;

    private static FakeDogmaDataAccessor Dogma() => new FakeDogmaDataAccessor()
        .Type(TargetSkill, 16, 16,
            new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 1),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Perception),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower));

    /// <summary>Criterion A2. Red if a scenario is saved: the two view models behind the panel never take a
    /// dispatcher or a write-capable client at all, so COPY AS TEXT only reaches the clipboard and PUT IT IN THE
    /// DOCTRINE only ever reaches the caller's own hand-off — never a command on its own.</summary>
    [Fact]
    public async Task ShareDialog_CopiesOrHandsOff_NeverDispatchesItself()
    {
        var offenders = new[] { typeof(SkillsWhatIfViewModel), typeof(SkillPlanShareDialogViewModel) }
            .SelectMany(type => type.GetConstructors().SelectMany(c => c.GetParameters()))
            .Select(p => p.ParameterType)
            .Where(t => t.Name.Contains("Dispatcher") || t.Name.Contains("CqrsSender") || t.Name.Contains("FleetCompositionClient"))
            .ToList();
        Assert.Empty(offenders); // neither view model can reach a write path except through the caller's own delegate

        var dialogs = new RecordingDialogService();
        var sde = new FakeSdeAccessor().Add(TargetSkill, "Test Skill", 16, 16);
        var rows = new List<SkillPlanRow> { new() { SkillTypeId = TargetSkill, Level = 1 } };
        bool putInDoctrineCalled = false;
        var vm = new SkillPlanShareDialogViewModel(dialogs, "Test Plan", rows, sde, () =>
        {
            putInDoctrineCalled = true;
            return Task.CompletedTask;
        });

        await vm.CopyAsTextCommand.ExecuteAsync(null);
        Assert.NotNull(dialogs.LastClipboardText);
        Assert.False(putInDoctrineCalled); // COPY AS TEXT never touches the doctrine hand-off
        Assert.Null(dialogs.LastCompositionEditor); // and nothing here ever opened a real editor on its own

        await vm.PutInDoctrineCommand.ExecuteAsync(null);
        Assert.True(putInDoctrineCalled); // only the explicit action reaches the caller's hand-off
        Assert.Null(dialogs.LastCompositionEditor); // the dialog itself still never opens one — that is the caller's job
    }

    /// <summary>Criterion A3. Red if a character grows the summary into a third line — "fly it today" and "soonest
    /// of the rest" are the whole of it, with 3 characters or 12.</summary>
    [Fact]
    public void OtherCharactersSummary_StaysTwoLines_With12Characters()
    {
        var dogma = Dogma();
        var validator = new FitValidator(dogma);
        var estimator = new SkillTrainingEstimator(dogma);
        var rows = new List<SkillPlanRow> { new() { SkillTypeId = TargetSkill, Level = 1 } };
        var attributes = new CharacterAttributeSet(20, 20, 20, 20, 20);

        var trained = Enumerable.Range(0, 3).Select(i => new CompositionCharacterSnapshot($"Trained {i}", true, false,
            new Dictionary<int, int> { [TargetSkill] = 1 }, attributes, []));
        var training = Enumerable.Range(0, 9).Select(i => new CompositionCharacterSnapshot($"Training {i}", true, false,
            new Dictionary<int, int>(), attributes, []));
        var others = trained.Concat(training).ToList();

        var summary = OtherCharactersCalculator.Compute(others, rows, validator, estimator, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("fly it today: 3", summary.FlyItTodayLine);
        Assert.StartsWith("soonest of the rest:", summary.SoonestLine);
        // The summary is exactly these two properties — there is no per-character collection to grow a third line into.
        Assert.Equal(2, typeof(OtherCharactersSummary).GetProperties().Count(p => p.Name is "FlyItTodayLine" or "SoonestLine"));
    }

    /// <summary>Criterion A4. Red if the EVE Workbench option is ever enabled — D8 says "later" until that export
    /// exists, and nothing in this dialog can turn it on.</summary>
    [Fact]
    public void EveWorkbenchOption_IsAlwaysDisabled_WithALaterHint()
    {
        var dialogs = new RecordingDialogService();
        var sde = new FakeSdeAccessor();
        var rows = new List<SkillPlanRow>();

        var vm = new SkillPlanShareDialogViewModel(dialogs, "Test Plan", rows, sde, () => Task.CompletedTask);

        Assert.False(vm.IsEwbEnabled);
        Assert.Equal("later", vm.EwbHint);
    }
}
