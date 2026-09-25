using System;
using System.Collections.Generic;
using EveUtils.Client.ViewModels.Skills.WhatIf;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Plans.Entities;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-358 A1: the five what-if scenarios, on a synthetic fixture with a fixed clock — never the real SDE. A rank-1
/// skill trained on Perception (primary) + Willpower (secondary), so a remap that maxes those two attributes visibly
/// beats the character's flat 20/20/20/20/20 split.
/// </summary>
public class WhatIfCalculatorTests
{
    private const int Head = 300;       // the queue's own position 0 — actively training right now
    private const int Queued = 200;     // queued behind it, not yet started
    private const int NotQueued = 100;  // not in the queue at all

    private static readonly DateTimeOffset Now = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly CharacterAttributeSet FlatAttributes = new(20, 20, 20, 20, 20);
    private static readonly CharacterAttributeSet NoImplants = new(0, 0, 0, 0, 0);

    private static FakeDogmaDataAccessor Dogma() => new FakeDogmaDataAccessor()
        .Type(Head, 16, 16,
            new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 1),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Perception),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower))
        .Type(Queued, 16, 16,
            new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 1),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Perception),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower))
        .Type(NotQueued, 16, 16,
            new SdeDogmaAttribute(DogmaAttributeIds.SkillTimeConstant, 1),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillPrimaryAttribute, DogmaAttributeIds.Perception),
            new SdeDogmaAttribute(DogmaAttributeIds.SkillSecondaryAttribute, DogmaAttributeIds.Willpower));

    /// <summary>Criterion A1. Red if implants are counted twice (ESI value used instead of Base()), scenario 1 is
    /// priced without the queue's own FinishDate, or "plan first" restarts the actively-training skill from scratch
    /// instead of crediting the progress its own FinishDate already reflects.</summary>
    [Fact]
    public void Compute_GivesFiveScenarios_StrictlyDescendingOrEqualFromOneToFive()
    {
        var dogma = Dogma();
        var planRows = new List<SkillPlanRow>
        {
            new() { SkillTypeId = Head, Level = 1 },
            new() { SkillTypeId = Queued, Level = 1 },
            new() { SkillTypeId = NotQueued, Level = 1 }
        };
        var queue = new List<CharacterSkillQueueEntry>
        {
            new() { SkillTypeId = Head, FinishedLevel = 1, QueuePosition = 0, FinishDate = Now.AddMinutes(5) },
            new() { SkillTypeId = Queued, FinishedLevel = 1, QueuePosition = 1, FinishDate = Now.AddMinutes(50) },
            new() { SkillTypeId = 999, FinishedLevel = 1, QueuePosition = 2, FinishDate = Now.AddMinutes(100) } // sets QueueEndsAt
        };

        var scenarios = WhatIfCalculator.Compute(planRows, queue, dogma, FlatAttributes, NoImplants, Now);

        Assert.Equal(5, scenarios.Count);
        // Scenario 1: the two queued rows land on their own FinishDate (5 and 50 min); the third trains after
        // QueueEndsAt (100 min) at 250 SP / 30 SP-per-min (20 + 20/2) = 8.3333 min -> 108.3333 min. Neither queued
        // row's date wins the max.
        Assert.Equal(Now.AddMinutes(108.3333), scenarios[0].Date, TimeSpan.FromSeconds(1));
        // Scenario 2: Head keeps its 5-minute FinishDate (already training — not a fresh 8.3333-minute estimate),
        // then Queued and NotQueued each train in full from there -> 5 + 8.3333 + 8.3333 = 21.6667 min.
        Assert.Equal(Now.AddMinutes(21.6667), scenarios[1].Date, TimeSpan.FromSeconds(1));

        Assert.True(scenarios[1].Date <= scenarios[0].Date);
        Assert.True(scenarios[2].Date <= scenarios[1].Date);
        Assert.True(scenarios[3].Date <= scenarios[2].Date);
        Assert.True(scenarios[4].Date <= scenarios[3].Date);

        // Scenario 3 (remap) must actually beat scenario 2's flat split: maxing Perception/Willpower is strictly
        // faster than 20/20 for a skill trained only on those two attributes.
        Assert.True(scenarios[2].Date < scenarios[1].Date);
        Assert.NotNull(scenarios[2].RemapSplit);

        // Scenario 4/5 (+4/+5 implants) can only ever match or beat scenario 3 — ImplantSetBonus.Apply never lowers
        // a bonus (Math.Max), so a red here means implants were subtracted instead of maxed in.
        Assert.True(scenarios[3].Date <= scenarios[2].Date);
        Assert.True(scenarios[4].Date <= scenarios[3].Date);

        // Isolated case, same criterion: a plan made of only the skill already training, one minute from done. A
        // fresh from-scratch estimate here (8.3333 min) would put "plan first" AFTER "as the queue stands" — the
        // exact ordering violation A1 forbids.
        var almostDoneRows = new List<SkillPlanRow> { new() { SkillTypeId = Head, Level = 1 } };
        var almostDoneQueue = new List<CharacterSkillQueueEntry>
        {
            new() { SkillTypeId = Head, FinishedLevel = 1, QueuePosition = 0, FinishDate = Now.AddMinutes(1) }
        };
        var almostDone = WhatIfCalculator.Compute(almostDoneRows, almostDoneQueue, dogma, FlatAttributes, NoImplants, Now);
        Assert.Equal(Now.AddMinutes(1), almostDone[0].Date, TimeSpan.FromSeconds(1));
        Assert.Equal(almostDone[0].Date, almostDone[1].Date, TimeSpan.FromSeconds(1));
    }
}
