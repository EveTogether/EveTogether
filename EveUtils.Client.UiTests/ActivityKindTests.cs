using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using UiDispatcher = Avalonia.Threading.Dispatcher;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-174: there is one <see cref="ActivityKind"/> and a run's kind is carried, never worked out again from "is it
/// abyssal". Each test below is the counter-proof for one acceptance criterion, and each was shown red against the
/// old shape first: the client's own two-member enum, and
/// <c>StoredKind =&gt; IsAbyssal ? Abyssal : Site</c>.
/// </summary>
public sealed class ActivityKindTests
{
    /// <summary>
    /// AC-1. Nothing that ships may read as a kind this build has never heard of. The window keeps a default arm so
    /// a run stored by a later version still opens (AGENTS.md §2), and this walks the enum to prove no kind is
    /// living on it — which is what turns "add a seventh kind" into a red test rather than a title nobody notices.
    /// </summary>
    [AvaloniaFact]
    public async Task EveryActivityKind_HasAHeaderOfItsOwn()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();

        foreach (ActivityKind kind in Enum.GetValues<ActivityKind>())
        {
            ActivityWindowViewModel window = await harness.OpenAsync(kind);
            Assert.NotEqual(ActivityWindowViewModel.UnknownKindHeader, window.HeaderTitle);
        }
    }

    /// <summary>AC-2 and AC-3. A window opened as a mission stores a mission. The old expression had no way to say
    /// so: every kind that was not abyssal was filed as a site, mission included.</summary>
    [AvaloniaFact]
    public async Task AMissionWindow_StoresItsRunAsAMission()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel window = await harness.OpenAsync(ActivityKind.Mission);

        await window.StartRunCommand.ExecuteAsync(null);

        Result<RunningRunDto> running = await harness.Services.GetRequiredService<Shared.Cqrs.IDispatcher>()
            .Query(new GetRunningRunQuery());
        Assert.True(running.IsSuccess, "START left no running run in the store");
        Assert.Equal(ActivityKind.Mission, running.Value!.ActivityKind);
    }

    /// <summary>AC-3, on the other side of the wire. The commander's kind reaches the member's window as it was
    /// sent; it used to be squeezed through "abyssal or else a site" on arrival.</summary>
    [AvaloniaFact]
    public void ACommandersMissionStart_OpensTheMembersWindowOnAMission()
    {
        var dialogs = new RecordingDialogService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IDialogService>(dialogs));
        instance.Services.GetRequiredService<Shared.Modules.Settings.Repositories.ISettingRepository>()
            .UpsertAsync(FleetRunWindowPresenter.AutoOpenSettingKey, "true").GetAwaiter().GetResult();
        var bus = instance.Services.GetRequiredService<IEventBus>();
        using var presenter = new FleetRunWindowPresenter(bus, dialogs, instance.Services);

        bus.PublishAsync(new FleetRunGroupCodeEvent(new RunGroupCodeStart(4242, ActivityKind.Mission, "MI-SS10",
            DateTime.UtcNow, IsFleetCommander: true))).GetAwaiter().GetResult();
        UiDispatcher.UIThread.RunJobs();

        Assert.Equal(ActivityKind.Mission, Assert.Single(dialogs.ShownActivityWindows).Kind);
    }

    /// <summary>AC-4. "Full clear" is a site's word and means nothing on a mission, so a mission is offered no list
    /// at all rather than one it half fits — and the row goes with it, because an empty question reads as a window
    /// that failed to load.</summary>
    [AvaloniaFact]
    public async Task AMissionWindow_IsOfferedNoLootStrategy()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel window = await harness.OpenAsync(ActivityKind.Mission);

        Assert.DoesNotContain("full clear", window.LootStrategies);
        Assert.Empty(window.LootStrategyChoices);
        Assert.False(window.IsLootStrategyShown);
    }
}
