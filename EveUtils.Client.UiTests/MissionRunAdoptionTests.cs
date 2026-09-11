using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-252: a window is not always the one that started the run it ends up showing — an app restart while a mission
/// run is going, or a second window opened on the same run, both adopt the row through
/// <c>ActivityWindowViewModel._AdoptRunningRunAsync</c> rather than being told about the mission directly the way a
/// clipboard copy is. <c>MissionAgentId</c>, <c>MissionLevel</c> and <c>PendingParameters</c> — the only things
/// <see cref="EveUtils.Client.ViewModels.Runs.Sections.MissionWindowSectionViewModel"/> ever reads — were only ever
/// set on that direct path, so an adopted run showed MISSION as "nothing recorded" even though its agent, level and
/// reward lines had been sitting on the row since the moment it started (Jithran, "Mining Misappropriation",
/// 2026-09-11 22:30 UTC — measured with three <c>RunParameter</c> rows already in <c>client.db</c>).
/// </summary>
public sealed class MissionRunAdoptionTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 11, 22, 30, 0, DateTimeKind.Utc);

    private static RunParameterInput[] _MiningMisappropriationRewards =>
    [
        new RunParameterInput { ParameterKey = RunParameterKey.Isk, TypedValue = "2460000", Amount = 2_460_000m, ObservedAtUtc = StartedAtUtc },
        new RunParameterInput { ParameterKey = RunParameterKey.LoyaltyPoints, TypedValue = "4,858", Amount = 4_858m, ObservedAtUtc = StartedAtUtc },
        new RunParameterInput
        {
            ParameterKey = RunParameterKey.BonusIsk, TypedValue = "2460000", Amount = 2_460_000m,
            BonusWindowSeconds = 19_260, ObservedAtUtc = StartedAtUtc // "within 5 hours and 21 minutes"
        }
    ];

    /// <summary>AC-1/AC-3: a fresh window — exactly what an app restart hands the pilot while the run is still
    /// going — adopts the run and shows what it actually earned, not "nothing recorded".</summary>
    [AvaloniaFact]
    public async Task AFreshWindow_AdoptingARunningMission_ShowsTheAgentLevelAndRewardsAlreadyOnTheRun()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Test Pilot", 90000001));

        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Mission, StartedAtUtc,
            0, "Mining Misappropriation", 30000142, SiteTypeSource: SiteTypeSource.Mission,
            AgentId: 3018841, MissionLevel: 2, Parameters: _MiningMisappropriationRewards));

        // Not the window that started it — the same shape an app restart, or a second window on the same run, gives.
        var reopened = new ActivityWindowViewModel(ActivityKind.Mission, instance.Services);
        await reopened.LoadAsync();

        Assert.Equal(ActivityRunState.Running, reopened.RunState);
        Assert.True(reopened.Mission().HasAgent);
        Assert.Equal("Level 2", reopened.Mission().LevelText);
        Assert.Null(reopened.Mission().RewardsEmptyText);
        Assert.Contains(reopened.Mission().RewardRows, row => row.Label == "ISK" && row.ValueText == "2,460,000");
        Assert.Contains(reopened.Mission().RewardRows, row => row.Label == "LOYALTY POINTS" && row.ValueText == "4,858");
        Assert.True(reopened.Mission().HasBonus);
        Assert.Equal("2,460,000 ISK", reopened.Mission().BonusValueText);
    }

    /// <summary>Not just a restart: a run left running by one window and picked up by another — say, opened again
    /// from the runs overview — must not lose the same facts either.</summary>
    [AvaloniaFact]
    public async Task ASecondWindow_OpenedOnTheSameRunningMission_AlsoShowsTheRewards()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Test Pilot", 90000001));

        var first = new ActivityWindowViewModel(ActivityKind.Mission, instance.Services);
        await first.LoadAsync();
        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Mission, StartedAtUtc,
            0, "Mining Misappropriation", 30000142, SiteTypeSource: SiteTypeSource.Mission,
            AgentId: 3018841, MissionLevel: 2, Parameters: _MiningMisappropriationRewards));

        var second = new ActivityWindowViewModel(ActivityKind.Mission, instance.Services);
        await second.LoadAsync();

        Assert.Equal(ActivityRunState.Running, second.RunState);
        Assert.Null(second.Mission().RewardsEmptyText);
        Assert.Contains(second.Mission().RewardRows, row => row.Label == "ISK" && row.ValueText == "2,460,000");
        Assert.True(second.Mission().HasBonus);
    }
}
