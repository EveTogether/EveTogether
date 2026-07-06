using System;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Transport;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fleet.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// multi-server aggregation: a character coupled to several servers sees every server's fleets, grouped per
/// server, in the My/Browser lists — not just the first server (the old <c>FirstOrDefault()</c> behaviour). Drives
/// the real <see cref="FleetsViewModel"/> over a faked transport (no gRPC) across two coupled servers and asserts the
/// per-server grouping plus that the nested-grouping window renders headless.
/// </summary>
public class FleetsMultiServerGroupingTests
{
    private const string ServerA = "srvA:7443";
    private const string ServerB = "srvB:7443";
    private const int CharA = 100;
    private const int CharB = 200;

    [AvaloniaFact]
    public async Task TwoCoupledServers_GroupMyAndBrowserFleetsPerServer_AndRender()
    {
        var transport = new RecordingFleetTransportClient();
        // Server A: a fleet I own (CharA is the creator). Server B: a public fleet to discover.
        transport.MyFleetsByServer[ServerA] = [Fleet(11, "Alpha Op", CharA, FleetVisibility.InviteOnly)];
        transport.OpenFleetsByServer[ServerB] = [Fleet(22, "Bravo Roam", 999, FleetVisibility.Public)];

        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IDialogService>(new RecordingDialogService());
        });

        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync(ServerA, new ClientSessionTokens("t", "r", "Lionear", CharA));
        await sessions.SaveAsync(ServerB, new ClientSessionTokens("t", "r", "Maricadie", CharB));

        var vm = new FleetsViewModel(instance.Services);
        for (var i = 0; i < 100 && vm.ServerGroups.Count < 2; i++)
            await Task.Delay(50);

        // Unified overview: one group per server. Server A carries the fleet I own there (IsMine); Server B carries
        // the discoverable fleet there — both in the single ServerGroups list, each tagged with its server.
        Assert.Equal(2, vm.ServerGroups.Count);

        var groupA = Assert.Single(vm.ServerGroups, g => g.ServerAddress == ServerA);
        var myRow = Assert.Single(groupA.Fleets);
        Assert.Equal("Alpha Op", myRow.Name);
        Assert.True(myRow.IsMine);
        Assert.Equal(ServerA, myRow.ServerAddress);

        var groupB = Assert.Single(vm.ServerGroups, g => g.ServerAddress == ServerB);
        var browserRow = Assert.Single(groupB.Fleets);
        Assert.Equal("Bravo Roam", browserRow.Name);
        Assert.False(browserRow.IsMine);

        // Visual proof — the nested per-server grouping renders.
        var window = new FleetsWindow(vm) { Width = 720, Height = 640 };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fleets-multiserver.png");
    }

    private static FleetInfo Fleet(long id, string name, int creator, FleetVisibility visibility) =>
        new(id, name, null, visibility, FleetState.Active, creator, null, null, DateTimeOffset.UnixEpoch, FleetActivation.Forming);
}
