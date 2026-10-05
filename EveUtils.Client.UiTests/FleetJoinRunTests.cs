using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Client.Views;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Joining the commander's run later than its start (ET-440): the commander's window repeats that the run is going,
/// and a member who missed the start — or was cut off by a discard — sees it on their own window and joins it.
/// </summary>
public class FleetJoinRunTests
{
    private const string NewGroupCode = "HF-N3W1";

    [AvaloniaFact]
    public async Task CommandersWindow_RepeatsThatTheRunIsGoing()
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync(kind: ActivityKind.Mining);

        for (int tick = 0; tick < 3; tick++)
            await fleet.Jithran.PublishMetricsAsync();

        FleetRunRunningEvent running = Assert.IsType<FleetRunRunningEvent>(
            fleet.Jithran.Wire.Sent.Last(sent => sent is FleetRunRunningEvent));
        Assert.Equal(FleetOfTwo.GroupCode, running.Data.GroupCode);
        Assert.True(running.Data.IsFleetCommander);
        Assert.DoesNotContain(fleet.Raymond.Wire.Sent, sent => sent is FleetRunRunningEvent);
        Assert.Contains(fleet.Raymond.Instance.Services.GetRequiredService<RunningFleetRuns>()
            .Of(FleetOfTwo.FleetId, DateTimeOffset.UtcNow), start => start.GroupCode == FleetOfTwo.GroupCode);
    }

    [AvaloniaFact]
    public async Task MemberCutOffByADiscard_JoinsTheCommandersNextRun_WithTheRunTheyHave()
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync(kind: ActivityKind.Mining, configure: services =>
        {
            services.AddSingleton<IDialogService>(new RecordingDialogService
            {
                OnConfirm = (_, _) => Task.FromResult(true),
                OnChoose = (_, _) => true,
            });
            services.AddSingleton<IToastService>(new RecordingToastService());
        });
        await fleet.MineAsync(fleet.Raymond, "Veldspar", 1000);
        await fleet.Jithran.Window.DiscardRunCommand.ExecuteAsync(null);
        await fleet.SettleAsync(() => fleet.Raymond.Window.RunState is ActivityRunState.Discarded);

        await fleet.Raymond.Instance.Services.GetRequiredService<IEventBus>().PublishAsync(new FleetRunRunningEvent(
            new RunGroupCodeStart(FleetOfTwo.FleetId, ActivityKind.Mining, NewGroupCode, DateTime.UtcNow.AddMinutes(-1),
                IsFleetCommander: true), FleetOfTwo.JithranId), EventTarget.Local);
        fleet.Raymond.Refresh();
        await FleetOfTwo.RunJobsAsync();

        ActivityWindowViewModel window = fleet.Raymond.Window;
        Assert.True(window.IsJoinFleetRunShown);
        _Shot(window, "et440-join-fleet-run-after-discard");

        await window.JoinRunningFleetRunCommand.ExecuteAsync(null);
        await FleetOfTwo.RunJobsAsync();

        Assert.Equal(ActivityRunState.Running, window.RunState);
        Assert.Equal(NewGroupCode, window.GroupCode);
        Assert.False(window.IsJoinFleetRunShown);
        await using ClientDbContext db = await fleet.Raymond.Instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        Run run = await db.Set<Run>().AsNoTracking().SingleAsync(candidate => candidate.CharacterId == FleetOfTwo.RaymondId);
        Assert.Equal(NewGroupCode, run.GroupCode);
        Assert.Equal(RunState.Running, run.State);
        Assert.Contains(window.Participants.SelectMany(participant => participant.MiningEntries), ore => ore.Units == 1000);
        _Shot(window, "et440-join-fleet-run-joined");
    }

    [AvaloniaFact]
    public async Task BeforeTheStart_TheCommandersWindow_CountsWhoIsReady()
    {
        RecordingFleetTransportClient transport = new();
        transport.MembersByFleet[FleetOfTwo.FleetId] =
        [
            new FleetMemberInfo(1, FleetOfTwo.JithranId, -1, -1, FleetRole.FleetCommander, false, IsConnected: true),
            new FleetMemberInfo(2, FleetOfTwo.RaymondId, -1, -1, FleetRole.SquadMember, false, IsConnected: true),
            new FleetMemberInfo(3, 90000004, -1, -1, FleetRole.SquadMember, false, IsConnected: false),
        ];
        Pilot jithran = await Pilot.CreateAsync(FleetOfTwo.JithranId, "Jithran", services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [FleetOfTwo.JithranId] = "Jithran", [FleetOfTwo.RaymondId] = "Raymond", [90000004] = "Moso Itonula",
            });
        }, ActivityKind.Mining);
        using (jithran)
        {
            await jithran.PublishMetricsAsync();
            await jithran.Instance.Services.GetRequiredService<IEventBus>().PublishAsync(new FleetMetricEvent(
                new EveUtils.Shared.Modules.Fleet.Dtos.MetricSample(FleetOfTwo.RaymondId, FleetOfTwo.FleetId,
                    EveUtils.Shared.Modules.Fleet.Metrics.MetricKind.Presence,
                    (double)EveUtils.Shared.Modules.Fleet.Metrics.PresenceState.InGame,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), FleetOfTwo.RaymondId), EventTarget.Local);

            ActivityWindowViewModel window = jithran.Window;
            await window.RefreshFleetCommandAsync(DateTime.UtcNow);
            FleetWindowSectionViewModel section = window.Sections.OfType<FleetWindowSectionViewModel>().Single();
            for (int attempt = 0; attempt < 100 && section.HeaderSummary?.StartsWith("2 of 3") != true; attempt++)
            {
                jithran.Refresh();
                await FleetOfTwo.RunJobsAsync();
            }

            Assert.Equal(ActivityRunState.NotStarted, window.RunState);
            Assert.StartsWith("2 of 3 ready · 1 offline", section.HeaderSummary);
            Assert.Equal("not connected to the server", section.Rows.Single(row => row.Name == "Moso Itonula").SubText);
            _Shot(window, "et440-fleet-status-before-start");
        }
    }

    private static void _Shot(ActivityWindowViewModel model, string name)
    {
        foreach (EveUtils.Client.ViewModels.Runs.Sections.RunWindowSection section in model.Sections)
            section.IsExpanded = section is FleetWindowSectionViewModel or MiningWindowSectionViewModel;
        ActivityWindow window = new(model) { Width = 600, Height = 760 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        OverlayShots.Capture(window, name);
    }
}
