using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.Home;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-378: a local fleet pushes no server fleet.changed, so the home only learned of its start or stop when
/// something unrelated happened to announce.</summary>
public sealed class HomeLocalFleetAnnounceTests
{
    private const int Owner = 95001680;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(long FleetId, ClientFleetService Fleets, HomeDashboardViewModel Home)> FormingLocalFleetOnHomeAsync(
        TestClientInstance instance)
    {
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Local FC", Owner), Ct);
        var fleets = instance.Services.GetRequiredService<ClientFleetService>();
        var created = await fleets.CreateLocalFleetAsync("Local Misc", null, Owner, Ct);
        Assert.True(created.IsSuccess);

        var home = new HomeDashboardViewModel(instance.Services, HomeNavigation.None, []);
        await home.LoadAsync();
        Assert.Equal("1 forming", home.Fleets.CountText);
        return (created.Value, fleets, home);
    }

    private static async Task<bool> BecomesAsync(HomeDashboardViewModel home, string countText)
    {
        for (int wait = 0; wait < 60 && home.Fleets.CountText != countText; wait++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25, Ct);
        }

        return home.Fleets.CountText == countText;
    }

    /// <summary>AC-1. START on a local fleet shows on the home after its settle, without anything else announcing.</summary>
    [AvaloniaFact]
    public async Task StartingALocalFleet_ShowsItAsActiveOnTheHome()
    {
        using var instance = TestClientInstance.Create();
        var (fleetId, fleets, home) = await FormingLocalFleetOnHomeAsync(instance);
        using var _ = home;

        Assert.True((await fleets.StartFleetAsync(fleetId, Owner, Ct)).IsSuccess);

        Assert.True(await BecomesAsync(home, "1 active"));
        Assert.False(home.Fleets.Cards.Single().IsForming);
    }

    /// <summary>AC-2. The auto-stop runs off the UI thread and still puts the fleet back on forming on the home.</summary>
    [AvaloniaFact]
    public async Task AnAutoStoppedLocalFleet_ShowsAsFormingOnTheHome()
    {
        using var instance = TestClientInstance.Create();
        var (fleetId, fleets, home) = await FormingLocalFleetOnHomeAsync(instance);
        using var _ = home;
        Assert.True((await fleets.StartFleetAsync(fleetId, Owner, Ct)).IsSuccess);
        Assert.True(await BecomesAsync(home, "1 active"));
        await instance.Services.GetRequiredService<IFleetRepository>()
            .TouchMemberSeenAsync(fleetId, Owner, DateTimeOffset.UtcNow - TimeSpan.FromDays(2), Ct);

        var stopped = await Task.Run(() => instance.Services.GetRequiredService<LocalFleetAutoStopService>()
            .ReconcileAsync(DateTimeOffset.UtcNow, Ct), Ct);

        Assert.Single(stopped);
        Assert.True(await BecomesAsync(home, "1 forming"));
        Assert.True(home.Fleets.Cards.Single().IsForming);
    }

    /// <summary>The event is the handler's, not a caller's: every lifecycle command raises it on the local bus once the
    /// change is saved, and a refused command raises nothing.</summary>
    [Fact]
    public async Task FleetLifecycleCommands_PublishTheirChangeOnTheLocalBus_OnlyOnSuccess()
    {
        using var instance = TestClientInstance.Create();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Local FC", Owner), Ct);
        List<FleetChangeKind> kinds = [];
        using var subscription = instance.Services.GetRequiredService<IEventBus>().Subscribe<FleetChangedEvent>((changed, _) =>
        {
            kinds.Add(changed.Data.Kind);
            return Task.CompletedTask;
        });
        var fleets = instance.Services.GetRequiredService<ClientFleetService>();

        var created = await fleets.CreateLocalFleetAsync("Local Misc", null, Owner, Ct);
        await fleets.StartFleetAsync(created.Value, Owner + 1, Ct);
        await fleets.StartFleetAsync(created.Value, Owner, Ct);
        await fleets.StopFleetAsync(created.Value, Owner, cancellationToken: Ct);
        await fleets.StartFleetAsync(created.Value, Owner, Ct);
        await fleets.ConcludeFleetAsync(created.Value, Owner, Ct);
        await fleets.DisbandFleetAsync(created.Value, Owner, Ct);

        Assert.Equal(
            [FleetChangeKind.Created, FleetChangeKind.Activated, FleetChangeKind.Stopped, FleetChangeKind.Activated,
                FleetChangeKind.Concluded, FleetChangeKind.Disbanded],
            kinds);
    }
}
