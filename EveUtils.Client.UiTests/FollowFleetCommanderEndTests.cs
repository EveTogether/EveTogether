using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Notifications;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Settings.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// A member's run follows the commander's SAVE or DISCARD after a ten-second countdown (ET-458): the clock is the
/// protection, Cancel leaves the run open, and with the setting off the member is only told.
/// </summary>
public class FollowFleetCommanderEndTests
{
    private static readonly DateTime LongAfterTheCountdown = DateTime.UtcNow.AddMinutes(5);

    [AvaloniaFact]
    public async Task CommandersSave_SettingOn_SavesTheMembersRunWhenTheCountdownRunsOut()
    {
        using FleetOfTwo fleet = await _FleetAsync(follow: "true");

        await _CommanderSavesAsync(fleet);
        await fleet.SettleAsync(() => fleet.Raymond.Window.IsFollowShown);

        Assert.Contains("The FC saved this run — saving in", fleet.Raymond.Window.FollowText);
        Assert.Equal("Save now", fleet.Raymond.Window.FollowActionLabel);
        Assert.Equal(RunState.Stopped, (await _RunOfAsync(fleet.Raymond)).State);

        fleet.Raymond.Window.TickFollowCountdown(LongAfterTheCountdown);
        await fleet.SettleAsync(() => fleet.Raymond.Window.RunState is ActivityRunState.Saved);

        Assert.Equal(RunState.Saved, (await _RunOfAsync(fleet.Raymond)).State);
    }

    [AvaloniaFact]
    public async Task CommandersSave_SaveNow_SavesBeforeTheCountdownEnds()
    {
        using FleetOfTwo fleet = await _FleetAsync(follow: "true");

        await _CommanderSavesAsync(fleet);
        await fleet.SettleAsync(() => fleet.Raymond.Window.IsFollowShown);
        await fleet.Raymond.Window.FollowNowCommand.ExecuteAsync(null);

        Assert.Equal(RunState.Saved, (await _RunOfAsync(fleet.Raymond)).State);
    }

    [AvaloniaFact]
    public async Task CommandersSave_Cancel_LeavesTheMembersRunOpenEvenWhenTheCountdownRunsOut()
    {
        using FleetOfTwo fleet = await _FleetAsync(follow: "true");

        await _CommanderSavesAsync(fleet);
        await fleet.SettleAsync(() => fleet.Raymond.Window.IsFollowShown);
        fleet.Raymond.Window.CancelFollowCommand.Execute(null);
        fleet.Raymond.Window.TickFollowCountdown(LongAfterTheCountdown);
        await FleetOfTwo.RunJobsAsync();

        Assert.False(fleet.Raymond.Window.IsFollowShown);
        Assert.NotEqual(RunState.Saved, (await _RunOfAsync(fleet.Raymond)).State);
    }

    [AvaloniaFact]
    public async Task CommandersSave_SettingOff_OnlyTellsTheMember()
    {
        using FleetOfTwo fleet = await _FleetAsync(follow: "false");

        await _CommanderSavesAsync(fleet);
        await fleet.SettleAsync(() => fleet.Raymond.Window.HasRunNotice);
        fleet.Raymond.Window.TickFollowCountdown(LongAfterTheCountdown);
        await FleetOfTwo.RunJobsAsync();

        Assert.Contains("The fleet commander saved this run", fleet.Raymond.Window.RunNoticeText);
        Assert.False(fleet.Raymond.Window.IsFollowShown);
        Assert.NotEqual(RunState.Saved, (await _RunOfAsync(fleet.Raymond)).State);
    }

    [AvaloniaFact]
    public async Task FollowSetting_NeverChosen_FollowsTheAutoJoinSetting()
    {
        using FleetOfTwo fleet = await _FleetAsync(follow: null, autoOpen: "true");

        await _CommanderSavesAsync(fleet);
        await fleet.SettleAsync(() => fleet.Raymond.Window.IsFollowShown);

        Assert.True(fleet.Raymond.Window.IsFollowShown);
    }

    [AvaloniaFact]
    public async Task CommandersDiscard_SettingOn_DiscardsTheMembersRunWhenTheCountdownRunsOut()
    {
        using FleetOfTwo fleet = await _FleetAsync(follow: "true");
        Guid unrelatedRunId = await _AddUnrelatedRunAsync(fleet.Raymond);

        await _CommanderDiscardsAsync(fleet);
        await fleet.SettleAsync(() => fleet.Raymond.Window.IsFollowShown);

        Assert.Contains("The FC discarded this run — discarding in", fleet.Raymond.Window.FollowText);
        Assert.Contains("thrown away with it", fleet.Raymond.Window.FollowText);

        fleet.Raymond.Window.TickFollowCountdown(LongAfterTheCountdown);
        await fleet.SettleAsync(() => _DeletedAsync(fleet.Raymond).GetAwaiter().GetResult());

        Assert.True(await _DeletedAsync(fleet.Raymond));
        Assert.Equal(RunState.Stopped, await _StateOfAsync(fleet.Raymond, unrelatedRunId));
        Assert.False(await _IsDeletedAsync(fleet.Raymond, unrelatedRunId));
    }

    [AvaloniaFact]
    public async Task CommandersDiscard_Cancel_KeepsTheMembersRunAsUnfinished()
    {
        using FleetOfTwo fleet = await _FleetAsync(follow: "true");

        await _CommanderDiscardsAsync(fleet);
        await fleet.SettleAsync(() => fleet.Raymond.Window.IsFollowShown);
        fleet.Raymond.Window.CancelFollowCommand.Execute(null);
        fleet.Raymond.Window.TickFollowCountdown(LongAfterTheCountdown);
        await FleetOfTwo.RunJobsAsync();

        Run run = await _RunOfAsync(fleet.Raymond);
        Assert.Null(run.DeletedAtUtc);
        Assert.Equal(RunState.Stopped, run.State);
    }

    [AvaloniaFact]
    public async Task CommandersDiscard_SettingOff_KeepsTheExistingPauseAndNotice()
    {
        using FleetOfTwo fleet = await _FleetAsync(follow: "false");

        await _CommanderDiscardsAsync(fleet);
        await fleet.SettleAsync(() => fleet.Raymond.Window.RunState is ActivityRunState.Discarded);
        fleet.Raymond.Window.TickFollowCountdown(LongAfterTheCountdown);
        await FleetOfTwo.RunJobsAsync();

        Assert.False(fleet.Raymond.Window.IsFollowShown);
        Assert.Contains("The fleet commander discarded this run", fleet.Raymond.Window.RunNoticeText);
        Run run = await _RunOfAsync(fleet.Raymond);
        Assert.Null(run.DeletedAtUtc);
        Assert.Equal(RunState.Stopped, run.State);
        Assert.Null(run.GroupCode);
    }

    [AvaloniaFact]
    public async Task CommandersDiscard_MemberAddedLootOfTheirOwn_WaitsForAnAnswerInsteadOfTheClock()
    {
        using FleetOfTwo fleet = await _FleetAsync(follow: "true");
        await fleet.LootAsync(fleet.Raymond, 50);

        await _CommanderDiscardsAsync(fleet);
        await fleet.SettleAsync(() => fleet.Raymond.Window.IsFollowShown);
        fleet.Raymond.Window.TickFollowCountdown(LongAfterTheCountdown);
        await FleetOfTwo.RunJobsAsync();

        Assert.Contains("only discarded if you say so", fleet.Raymond.Window.FollowText);
        Assert.Equal("Keep my run", fleet.Raymond.Window.FollowCancelLabel);
        Assert.False(await _DeletedAsync(fleet.Raymond));

        await fleet.Raymond.Window.FollowNowCommand.ExecuteAsync(null);

        Assert.True(await _DeletedAsync(fleet.Raymond));
    }

    [Fact]
    public void AnOlderClient_DropsTheSavedEventItDoesNotKnow()
    {
        string payload = JsonSerializer.Serialize(
            new RunGroupSave(FleetOfTwo.FleetId, ActivityKind.Site, FleetOfTwo.GroupCode, DateTime.UtcNow));
        EventTypeRegistry olderClient = new();
        EventTypeRegistry current = new();
        new FleetWireEvents().RegisterInto(current);

        Assert.Null(olderClient.Deserialize("fleet.run-saved", payload, null));
        Assert.IsType<FleetRunSavedEvent>(current.Deserialize("fleet.run-saved", payload, null));
    }

    [AvaloniaFact]
    public async Task OlderMember_ReceivingTheSavedEvent_IgnoresItAndKeepsItsRun()
    {
        Pilot jithran = await Pilot.CreateAsync(FleetOfTwo.JithranId, "Jithran");
        Pilot olderRaymond = await Pilot.CreateAsync(FleetOfTwo.RaymondId, "Raymond", services =>
            services.AddSingleton<IEventTypeRegistry>(new EventTypeRegistry()));
        using (jithran)
        using (olderRaymond)
        {
            jithran.Wire.Destinations.Add(olderRaymond.Instance.Services);
            RunGroupCodeStart start = new(FleetOfTwo.FleetId, ActivityKind.Site, FleetOfTwo.GroupCode,
                DateTime.UtcNow.AddMinutes(-2), IsFleetCommander: true);
            await jithran.JoinAsync(start);
            await olderRaymond.JoinAsync(start);

            await jithran.Window.SaveRunCommand.ExecuteAsync(null);
            await FleetOfTwo.RunJobsAsync();

            Assert.Contains(jithran.Wire.Sent, sent => sent is FleetRunSavedEvent);
            Assert.False(olderRaymond.Window.IsFollowShown);
            Assert.NotEqual(RunState.Saved, (await _RunOfAsync(olderRaymond)).State);
        }
    }

    [Fact]
    public void Resolve_PilotsOwnChoiceBeatsTheAutoJoinSetting()
    {
        Dictionary<string, string> chosenOff = new()
        {
            [FleetRunWindowPresenter.AutoOpenSettingKey] = "true",
            [FollowFleetCommanderEnd.SettingKey] = "false"
        };
        Dictionary<string, string> fleetOverride = new()
        {
            [FleetRunWindowPresenter.AutoOpenSettingKey] = "false",
            [FleetRunWindowPresenter.PerFleetAutoOpenSettingKey(7)] = "true"
        };

        Assert.False(FollowFleetCommanderEnd.Resolve(chosenOff, 7));
        Assert.True(FollowFleetCommanderEnd.Resolve(fleetOverride, 7));
        Assert.False(FollowFleetCommanderEnd.Resolve(fleetOverride, 8));
        Assert.False(FollowFleetCommanderEnd.Resolve(new Dictionary<string, string>(), 7));
    }

    private static async Task<FleetOfTwo> _FleetAsync(string? follow, string? autoOpen = null)
    {
        FleetOfTwo fleet = await FleetOfTwo.CreateAsync(configure: services =>
        {
            services.AddSingleton<IDialogService>(new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) });
            services.AddSingleton<IToastService>(new RecordingToastService());
        });
        IDispatcher dispatcher = fleet.Raymond.Instance.Services.GetRequiredService<IDispatcher>();
        if (follow is not null)
            await dispatcher.Send(new SetSettingCommand(FollowFleetCommanderEnd.SettingKey, follow));
        if (autoOpen is not null)
            await dispatcher.Send(new SetSettingCommand(FleetRunWindowPresenter.AutoOpenSettingKey, autoOpen));
        return fleet;
    }

    private static async Task _CommanderSavesAsync(FleetOfTwo fleet)
    {
        await fleet.Jithran.Window.SaveRunCommand.ExecuteAsync(null);
        await FleetOfTwo.RunJobsAsync();
    }

    private static async Task _CommanderDiscardsAsync(FleetOfTwo fleet)
    {
        await fleet.Jithran.Window.DiscardRunCommand.ExecuteAsync(null);
        await fleet.SettleAsync(() => fleet.Raymond.Window.RunState is ActivityRunState.Discarded);
    }

    private static async Task<Run> _RunOfAsync(Pilot pilot)
    {
        await using ClientDbContext db = await pilot.Instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return await db.Set<Run>().AsNoTracking().OrderBy(run => run.StartedAtUtc)
            .FirstAsync(candidate => candidate.CharacterId == pilot.CharacterId && candidate.ActivityKind == ActivityKind.Site
                                     && candidate.SiteName != "Unrelated");
    }

    private static async Task<bool> _DeletedAsync(Pilot pilot)
    {
        await using ClientDbContext db = await pilot.Instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return await db.Set<Run>().AsNoTracking()
            .Where(candidate => candidate.CharacterId == pilot.CharacterId && candidate.SiteName != "Unrelated")
            .AllAsync(candidate => candidate.DeletedAtUtc != null);
    }

    private static async Task<Guid> _AddUnrelatedRunAsync(Pilot pilot)
    {
        await using ClientDbContext db = await pilot.Instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        Run unrelated = new()
        {
            Id = Guid.NewGuid(),
            CharacterId = pilot.CharacterId,
            ActivityKind = ActivityKind.Site,
            State = RunState.Stopped,
            StartedAtUtc = DateTime.UtcNow.AddHours(-3),
            StoppedAtUtc = DateTime.UtcNow.AddHours(-2),
            SiteName = "Unrelated"
        };
        db.Set<Run>().Add(unrelated);
        await db.SaveChangesAsync();
        return unrelated.Id;
    }

    private static async Task<RunState> _StateOfAsync(Pilot pilot, Guid runId)
    {
        await using ClientDbContext db = await pilot.Instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return (await db.Set<Run>().AsNoTracking().SingleAsync(run => run.Id == runId)).State;
    }

    private static async Task<bool> _IsDeletedAsync(Pilot pilot, Guid runId)
    {
        await using ClientDbContext db = await pilot.Instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return (await db.Set<Run>().AsNoTracking().SingleAsync(run => run.Id == runId)).DeletedAtUtc is not null;
    }
}
