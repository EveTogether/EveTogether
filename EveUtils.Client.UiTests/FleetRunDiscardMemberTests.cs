using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Notifications;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The fleet commander discards the fleet run (ET-440): a member's run stops and leaves the group, and stays stopped —
/// on 2026-10-05 Moso's run was found still running on its own, unlinked from HF-JL33, minutes after Jithran's discard.
/// </summary>
public class FleetRunDiscardMemberTests
{
    [AvaloniaFact]
    public async Task CommandersDiscard_StopsTheMembersRun_AndItStaysStopped()
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync(kind: ActivityKind.Mining, configure: services =>
        {
            services.AddSingleton<IDialogService>(new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) });
            services.AddSingleton<IToastService>(new RecordingToastService());
        });
        await fleet.MineAsync(fleet.Raymond, "Veldspar", 1000);

        await fleet.Jithran.Window.DiscardRunCommand.ExecuteAsync(null);
        await fleet.SettleAsync(() => fleet.Raymond.Window.RunState is ActivityRunState.Discarded);

        Assert.Equal(ActivityRunState.Discarded, fleet.Raymond.Window.RunState);
        await _AssertStoppedAndUnlinkedAsync(fleet.Raymond);

        await fleet.MineAsync(fleet.Raymond, "Veldspar", 500);
        for (int tick = 0; tick < 5; tick++)
            await fleet.Raymond.PublishMetricsAsync();
        await _AssertStoppedAndUnlinkedAsync(fleet.Raymond);

        RecordingToastService toasts = (RecordingToastService)fleet.Jithran.Instance.Services.GetRequiredService<IToastService>();
        toasts.ActionToasts.Last(toast => toast.Title == "Run thrown away").Actions.Single(action => action.Label == "Undo").Run();
        await FleetOfTwo.RunJobsAsync();
        await _AssertStoppedAndUnlinkedAsync(fleet.Raymond);
    }

    private static async Task _AssertStoppedAndUnlinkedAsync(Pilot pilot)
    {
        await using ClientDbContext db = await pilot.Instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        Run run = await db.Set<Run>().AsNoTracking().SingleAsync(candidate => candidate.CharacterId == pilot.CharacterId);
        Assert.Equal(RunState.Stopped, run.State);
        Assert.Null(run.GroupCode);
        Assert.Equal(FleetOfTwo.GroupCode, run.FormerGroupCode);
    }
}
