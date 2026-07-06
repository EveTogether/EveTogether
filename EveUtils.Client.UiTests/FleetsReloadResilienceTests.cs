using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Transport;
using EveUtils.Shared.Modules.Fleet.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// A transient transport failure must not blank the Fleets window. Earlier the listing cleared its collections and
/// then mapped a failed gRPC call to an empty list, so a flaky connection made fleets "disappear" and a Refresh that
/// hit the same error kept them gone — the exact Windows symptom (the per-RPC channel leak made those failures common
/// there). ReloadAsync now keeps the current rows on a <see cref="FleetTransportException"/> and reports it.
/// </summary>
public class FleetsReloadResilienceTests
{
    private const string Server = "srv:7443";
    private const int Char = 100;

    [AvaloniaFact]
    public async Task TransientTransportFailure_KeepsCurrentFleets_AndReportsIt()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Fleet(11, "Alpha Op", Char, FleetVisibility.InviteOnly)];

        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IDialogService>(new RecordingDialogService());
        });

        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", "Lionear", Char));

        var vm = new FleetsViewModel(instance.Services);
        for (var i = 0; i < 100 && vm.ServerGroups.Count == 0; i++)
            await Task.Delay(50);
        Assert.Single(vm.ServerGroups); // baseline: the fleet is listed.

        // The server goes unreachable; a Refresh now fails its sweep.
        transport.UnreachableServers.Add(Server);
        await vm.RefreshCommand.ExecuteAsync(null);

        // The row is kept (not blanked) and the failure is surfaced rather than read as "no fleets".
        var group = Assert.Single(vm.ServerGroups);
        Assert.Equal("Alpha Op", Assert.Single(group.Fleets).Name);
        Assert.Contains("Could not reach", vm.StatusMessage);

        // Recovery: once the server is reachable again, a Refresh repopulates normally.
        transport.UnreachableServers.Remove(Server);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Alpha Op", Assert.Single(Assert.Single(vm.ServerGroups).Fleets).Name);
    }

    [AvaloniaFact]
    public async Task OneServerUnreachable_StillShowsTheOtherServersFleets()
    {
        const string Good = "good:7443";
        const string Down = "down:7443";

        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Good] = [Fleet(11, "Alpha Op", Char, FleetVisibility.InviteOnly)];
        transport.MyFleetsByServer[Down] = [Fleet(22, "Bravo Op", Char, FleetVisibility.InviteOnly)];
        transport.UnreachableServers.Add(Down); // the second server is down — it must not block the first.

        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IDialogService>(new RecordingDialogService());
        });

        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync(Good, new ClientSessionTokens("t", "r", "Lionear", Char));
        await sessions.SaveAsync(Down, new ClientSessionTokens("t", "r", "Lionear", Char));

        var vm = new FleetsViewModel(instance.Services);
        for (var i = 0; i < 100 && vm.ServerGroups.Count == 0; i++)
            await Task.Delay(50);

        // The reachable server's fleet shows even though the other server is unreachable; the down one is reported.
        var group = Assert.Single(vm.ServerGroups);
        Assert.Equal(Good, group.ServerAddress);
        Assert.Equal("Alpha Op", Assert.Single(group.Fleets).Name);
        Assert.Contains("Could not reach", vm.StatusMessage);

        // The unreachable server is surfaced with a decouple row (so a stale, fleet-less server can be removed).
        Assert.True(vm.HasUnreachableServers);
        Assert.Equal(Down, Assert.Single(vm.UnreachableServers).ServerAddress);
    }

    private static FleetInfo Fleet(long id, string name, int creator, FleetVisibility visibility) =>
        new(id, name, null, visibility, FleetState.Active, creator, null, null, DateTimeOffset.UnixEpoch, FleetActivation.Forming);
}
