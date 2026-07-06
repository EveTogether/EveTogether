using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Transport;
using EveUtils.Shared.Modules.Fleet.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Regression for the live bug where fleets that already existed on the server were not shown until the client was
/// restarted: the window's construction-time load can run while a server's bus connection is still establishing and
/// come back empty, and only a fleet.changed event re-fetched the list — existing fleets emit none. The listing now
/// reloads when a server reaches Connected, so the fleets appear without reopening or restarting.
/// </summary>
public class FleetsConnectionReloadTests
{
    private const string Server = "srv:7443";
    private const int Char = 100;

    [AvaloniaFact]
    public async Task ServerReachingConnected_ReloadsFleets_WithoutRestart()
    {
        // The fleet exists on the server, but the bus connection is not ready when the window opens (modelled as the
        // initial sweep finding the server unreachable), so the construction-time load shows nothing.
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Fleet(11, "Alpha Op", Char)];
        transport.UnreachableServers.Add(Server);
        var connector = new FakeRemoteBusConnector();

        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IRemoteBusConnector>(connector);
            services.AddSingleton<IDialogService>(new RecordingDialogService());
        });

        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", "Lionear", Char));

        var vm = new FleetsViewModel(instance.Services);
        for (var i = 0; i < 100 && !vm.HasUnreachableServers; i++)
            await Task.Delay(50);
        Assert.True(vm.HasUnreachableServers);   // the construction-time load saw no fleets
        Assert.Empty(vm.ServerGroups);

        // The bus connection reaches Connected; the fleet is now fetchable.
        transport.UnreachableServers.Remove(Server);
        connector.RaiseStateChanged(Server, ServerConnectionState.Connected);

        for (var i = 0; i < 100 && vm.ServerGroups.Count == 0; i++)
            await Task.Delay(50);
        Assert.Equal("Alpha Op", Assert.Single(Assert.Single(vm.ServerGroups).Fleets).Name);
    }

    private static FleetInfo Fleet(long id, string name, int creator) =>
        new(id, name, null, FleetVisibility.InviteOnly, FleetState.Active, creator, null, null,
            DateTimeOffset.UnixEpoch, FleetActivation.Forming);
}
