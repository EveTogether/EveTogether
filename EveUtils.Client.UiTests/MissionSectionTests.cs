using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-237: the MISSION section in the run window — agent, level, and what a mission handed out, with a bonus
/// countdown that drops the bonus out of TOTAL ISK once its window has passed. The detail screen's half of the same
/// section is covered in <c>ActivityDetailTests</c>, beside the rest of that screen's own coverage.
/// </summary>
public sealed class MissionSectionTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    // ── The agent ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A regular agent's mission has no "Report to" line at all — the section says so honestly rather than
    /// showing an id it does not have (ET-237, mission-captures.md comment 1).</summary>
    [Fact]
    public void NoAgentInTheCapture_SaysSoRatherThanShowingAnId()
    {
        var window = new ActivityWindowViewModel(ActivityKind.Mission, new ServiceCollection().BuildServiceProvider());
        window.Refresh(NowUtc);

        Assert.False(window.Mission().HasAgent);
        Assert.Equal("not stated in this capture", window.Mission().AgentText);
        Assert.False(window.Mission().IsLevelShown);
    }

    /// <summary>An epic-arc agent's capture does carry one — named through the SDE rather than left as the bare id
    /// this screen used to show (ET-237 acceptance 2).</summary>
    [Fact]
    public void AgentInTheCapture_IsNamedThroughTheSde()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().AddAgent(new SdeAgent(3019407, "Aralin Jick",
            Level: 4, AgentTypeId: 10, AgentTypeName: "EpicArcAgent", DivisionId: 1, IsLocator: false,
            CorporationId: 1000089, LocationId: 60008689, SolarSystemId: 30005040, SolarSystemName: "Nishah")));
        var window = new ActivityWindowViewModel(ActivityKind.Mission, services.BuildServiceProvider())
        {
            MissionAgentId = 3019407, MissionLevel = 4
        };
        window.Refresh(NowUtc);

        Assert.True(window.Mission().HasAgent);
        Assert.Equal("Aralin Jick", window.Mission().AgentText);
        Assert.Equal("Level 4", window.Mission().LevelText);
    }

    // ── The important-mission flag (ET-251) ───────────────────────────────────────────────────────

    /// <summary>An important mission's own <see cref="RunParameterKey.ImportantMission"/> parameter marks the run
    /// rather than showing as a reward row.</summary>
    [Fact]
    public void ImportantMissionParameter_IsShownAsTheFlag_NotAsARewardRow()
    {
        var window = new ActivityWindowViewModel(ActivityKind.Mission, new ServiceCollection().BuildServiceProvider())
        {
            PendingParameters =
            [
                new RunParameterInput { ParameterKey = RunParameterKey.ImportantMission, TypedValue = "important mission", ObservedAtUtc = NowUtc },
                new RunParameterInput { ParameterKey = RunParameterKey.Item, TypedValue = "1 x Cybernetic Subprocessor - Standard", Amount = 1m, ObservedAtUtc = NowUtc }
            ]
        };

        window.Refresh(NowUtc);

        Assert.True(window.Mission().IsImportantMission);
        Assert.Single(window.Mission().RewardRows);
        Assert.Contains("important · affects faction standing", window.Mission().HeaderSummary);
    }

    /// <summary>A mission with no such parameter reads as not important — nothing is guessed from the reward shape
    /// alone.</summary>
    [Fact]
    public void NoImportantMissionParameter_ReadsAsNotImportant()
    {
        var window = new ActivityWindowViewModel(ActivityKind.Mission, new ServiceCollection().BuildServiceProvider());
        window.Refresh(NowUtc);

        Assert.False(window.Mission().IsImportantMission);
    }

    // ── The rewards and the object-initialiser build order ────────────────────────────────────────

    /// <summary>The window is constructed, then handed its <c>PendingParameters</c> through an object initialiser
    /// (<c>ClipboardMissionOffer._StartRunAsync</c>'s own shape) — after the section itself was already built. The
    /// section must still show them once the window is used, not stay empty because it read them too early.</summary>
    [Fact]
    public void RewardsSetAfterConstruction_StillReachTheSection()
    {
        var window = new ActivityWindowViewModel(ActivityKind.Mission, new ServiceCollection().BuildServiceProvider())
        {
            PendingParameters =
            [
                new RunParameterInput { ParameterKey = RunParameterKey.LoyaltyPoints, TypedValue = "1,150", Amount = 1_150m, ObservedAtUtc = NowUtc },
                new RunParameterInput { ParameterKey = RunParameterKey.Unknown, TypedValue = "Mysterious reward", ObservedAtUtc = NowUtc }
            ]
        };

        window.Refresh(NowUtc);

        Assert.Equal(2, window.Mission().RewardRows.Count);
        Assert.Contains(window.Mission().RewardRows, row => row.Label == "LOYALTY POINTS" && row.ValueText == "1,150");
        Assert.Contains(window.Mission().RewardRows, row => row.ValueText == "Mysterious reward");
    }

    // ── The bonus countdown ────────────────────────────────────────────────────────────────────────

    /// <summary>Acceptance: a mission with a bonus window shows a counting-down timer, and TOTAL ISK includes the
    /// bonus while it is still live.</summary>
    [Fact]
    public void LiveBonus_CountsDown_AndStaysInTotalIsk()
    {
        DateTime copiedAtUtc = NowUtc.AddMinutes(-10);
        var window = new ActivityWindowViewModel(ActivityKind.Mission, new ServiceCollection().BuildServiceProvider())
        {
            PendingParameters =
            [
                new RunParameterInput { ParameterKey = RunParameterKey.Isk, TypedValue = "360000", Amount = 360_000m, ObservedAtUtc = copiedAtUtc },
                new RunParameterInput
                {
                    ParameterKey = RunParameterKey.BonusIsk, TypedValue = "347000", Amount = 347_000m,
                    BonusWindowSeconds = 4560, ObservedAtUtc = copiedAtUtc // "within 1 hour and 16 minutes" — 66 minutes total, 10 gone
                }
            ]
        };

        window.Refresh(NowUtc);

        Assert.False(window.Mission().IsBonusExpired);
        Assert.Equal("expires in 1:06:00", window.Mission().BonusCountdownText); // 76-minute window, 10 gone
        Assert.Equal("707,000 ISK", window.GroupTotalIskText); // 360,000 + 347,000, bonus still live
    }

    /// <summary>Acceptance: once the bonus window has passed it reads as expired and drops out of TOTAL ISK — the
    /// plain ISK reward keeps counting, since only the bonus carries a deadline.</summary>
    [Fact]
    public void ExpiredBonus_ReadsAsExpired_AndDropsOutOfTotalIsk()
    {
        DateTime copiedAtUtc = NowUtc.AddMinutes(-90);
        var window = new ActivityWindowViewModel(ActivityKind.Mission, new ServiceCollection().BuildServiceProvider())
        {
            PendingParameters =
            [
                new RunParameterInput { ParameterKey = RunParameterKey.Isk, TypedValue = "360000", Amount = 360_000m, ObservedAtUtc = copiedAtUtc },
                new RunParameterInput
                {
                    ParameterKey = RunParameterKey.BonusIsk, TypedValue = "347000", Amount = 347_000m,
                    BonusWindowSeconds = 4560, ObservedAtUtc = copiedAtUtc
                }
            ]
        };

        window.Refresh(NowUtc);

        Assert.True(window.Mission().IsBonusExpired);
        Assert.Equal("bonus expired", window.Mission().BonusCountdownText);
        Assert.Equal("360,000 ISK", window.GroupTotalIskText); // the bonus dropped out, the plain ISK stayed
    }

    /// <summary>Acceptance: a run stopped inside the window keeps its bonus even if the window sits open, unsaved,
    /// past the deadline — the judgement freezes at STOP, not at whenever SAVE happens to be pressed.</summary>
    [Fact]
    public void BonusMetBeforeStop_StaysEarned_EvenAfterTheDeadlinePassesOnAnOpenWindow()
    {
        DateTime copiedAtUtc = NowUtc.AddMinutes(-10); // deadline at NowUtc + 56 minutes
        var window = new ActivityWindowViewModel(ActivityKind.Mission, new ServiceCollection().BuildServiceProvider())
        {
            PendingParameters =
            [
                new RunParameterInput
                {
                    ParameterKey = RunParameterKey.BonusIsk, TypedValue = "347000", Amount = 347_000m,
                    BonusWindowSeconds = 4560, ObservedAtUtc = copiedAtUtc
                }
            ],
            StoppedAtUtc = NowUtc // stopped well inside the window
        };

        window.Refresh(NowUtc.AddHours(3)); // long after the deadline, window still open before SAVE

        Assert.False(window.Mission().IsBonusExpired);
        Assert.Equal("347,000 ISK", window.GroupTotalIskText);
    }

    // ── The loot strategy (ET-237 comment 3) ──────────────────────────────────────────────────────

    /// <summary>A mission run gets the same four loot strategies as a site, in the same order — optional with no
    /// preselection, since a courier has nothing to blitz or clear.</summary>
    [Fact]
    public void GetsTheSameFourLootStrategiesAsASite_OptionalWithNoPreselection()
    {
        var window = new ActivityWindowViewModel(ActivityKind.Mission, new ServiceCollection().BuildServiceProvider());
        window.Refresh(NowUtc);

        Assert.Equal(RunTypeCatalogue.SiteLootStrategies, window.Activity().LootStrategies);
        Assert.True(window.Activity().IsLootStrategyShown);
        Assert.Null(window.Activity().LootStrategy);
    }

    // ── The real view renders ──────────────────────────────────────────────────────────────────────

    /// <summary>Proves the XAML itself — every binding in <c>MissionWindowSectionView.axaml</c> resolves against
    /// the real view model rather than only against what a view-model-only test can see.</summary>
    [AvaloniaFact]
    public void MissionWindowSectionView_RendersAgentAndRewardsAndTheBonusCountdown()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().AddAgent(new SdeAgent(3019407, "Aralin Jick",
            Level: 4, AgentTypeId: 10, AgentTypeName: "EpicArcAgent", DivisionId: 1, IsLocator: false,
            CorporationId: 1000089, LocationId: 60008689, SolarSystemId: 30005040, SolarSystemName: "Nishah")));
        var window = new ActivityWindowViewModel(ActivityKind.Mission, services.BuildServiceProvider())
        {
            MissionAgentId = 3019407,
            MissionLevel = 4,
            PendingParameters =
            [
                new RunParameterInput { ParameterKey = RunParameterKey.Isk, TypedValue = "360000", Amount = 360_000m, ObservedAtUtc = NowUtc },
                new RunParameterInput { ParameterKey = RunParameterKey.LoyaltyPoints, TypedValue = "1,150", Amount = 1_150m, ObservedAtUtc = NowUtc },
                new RunParameterInput
                {
                    ParameterKey = RunParameterKey.BonusIsk, TypedValue = "347000", Amount = 347_000m,
                    BonusWindowSeconds = 4560, ObservedAtUtc = NowUtc.AddMinutes(-90) // already past the deadline
                },
                new RunParameterInput { ParameterKey = RunParameterKey.Unknown, TypedValue = "Mysterious reward", ObservedAtUtc = NowUtc }
            ]
        };
        window.Refresh(NowUtc);
        foreach (var section in window.Sections)
            section.IsExpanded = true;

        var view = new ActivityWindow(window);
        view.Show();
        Dispatcher.UIThread.RunJobs();
        view.Width = 560;
        view.Height = 900;
        Dispatcher.UIThread.RunJobs();
        List<string> texts = RenderedText.VisibleTexts(view);

        Assert.Contains(texts, text => text == "MISSION");
        Assert.Contains(texts, text => text == "Aralin Jick");
        Assert.Contains(texts, text => text == "Level 4");
        Assert.Contains(texts, text => text == "Mysterious reward");
        Assert.Contains(texts, text => text == "1,150");
        Assert.Contains(texts, text => text == "bonus expired");
    }

    // ── No section for a type that is not a mission ───────────────────────────────────────────────

    /// <summary>Acceptance 4: a combat site or an abyssal pocket has no MISSION section.</summary>
    [Theory]
    [InlineData(ActivityKind.Site)]
    [InlineData(ActivityKind.Abyssal)]
    public void OtherKinds_HaveNoMissionSection(ActivityKind kind)
    {
        var window = new ActivityWindowViewModel(kind, new ServiceCollection().BuildServiceProvider());

        Assert.DoesNotContain(window.Sections, section => section.Id == RunSectionId.Mission);
    }
}
