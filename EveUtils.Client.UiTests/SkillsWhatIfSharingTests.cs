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
}
