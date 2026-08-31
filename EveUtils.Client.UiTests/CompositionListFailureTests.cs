using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// A list read that never landed must never read as "there is nothing here" (ET-77). While the server refused the
/// session, `ListAllFleetCompositions` came back `Ok = false` and the client folded that into an empty list, so the
/// Compositions window cheerfully reported "No compositions shared on this server yet." — the one sentence that
/// makes a broken pairing invisible. These cover the distinction from both sides: the same empty grid has to carry a
/// different explanation depending on whether the read succeeded.
/// </summary>
public class CompositionListFailureTests
{
    private const string Server = "https://eve-together.com";
    private const int Char = 90250177;

    private static (CompositionsViewModel Vm, RecordingFleetTransportClient Transport) BuildAsync(
        TestClientInstance instance, RecordingFleetTransportClient transport)
    {
        var vm = new CompositionsViewModel(instance.Services);
        return (vm, transport);
    }

    private static async Task<CompositionTabViewModel> ServerTabAsync(CompositionsViewModel vm)
    {
        CompositionTabViewModel? tab = null;
        for (var i = 0; i < 100 && tab is null; i++)
        {
            tab = vm.Tabs.FirstOrDefault(t => !t.IsLocal);
            if (tab is null) await Task.Delay(50);
        }
        Assert.NotNull(tab);
        await tab!.ReloadAsync();
        return tab;
    }

    private static async Task<TestClientInstance> CoupledInstanceAsync(
        RecordingFleetTransportClient transport, FakeRemoteBusConnector connector)
    {
        var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IRemoteBusConnector>(connector);
            services.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(Server, new ClientSessionTokens("t", "r", "Jithran", Char));
        connector.RaiseStateChanged(Server, ServerConnectionState.Connected);
        return instance;
    }

    [AvaloniaFact]
    public async Task AnEmptyLibrary_SaysThereAreNone()
    {
        var transport = new RecordingFleetTransportClient();
        var connector = new FakeRemoteBusConnector();
        using var instance = await CoupledInstanceAsync(transport, connector);

        var (vm, _) = BuildAsync(instance, transport);
        var tab = await ServerTabAsync(vm);

        Assert.Empty(tab.Loaded);
        Assert.Contains("No compositions shared on this server yet", tab.Status);
    }

    [AvaloniaFact]
    public async Task AFailedRead_SaysItCouldNotLoad_AndNotThatThereAreNone()
    {
        var transport = new RecordingFleetTransportClient();
        transport.UnreachableServers.Add(Server); // the server refuses the session — the read never lands
        var connector = new FakeRemoteBusConnector();
        using var instance = await CoupledInstanceAsync(transport, connector);

        var (vm, _) = BuildAsync(instance, transport);
        var tab = await ServerTabAsync(vm);

        Assert.Empty(tab.Loaded); // the grid looks identical…
        Assert.Contains("Couldn't load", tab.Status); // …the explanation must not be
        Assert.Contains("Not authenticated", tab.Status); // the server's own words, carried through
        Assert.DoesNotContain("No compositions shared on this server yet", tab.Status);
    }

    [AvaloniaFact]
    public async Task AStockedLibrary_StillLoads()
    {
        var transport = new RecordingFleetTransportClient();
        transport.CompositionsByServer[Server] =
            [new FleetCompositionInfo(7, "Armor HAW", null, Char, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)];
        var connector = new FakeRemoteBusConnector();
        using var instance = await CoupledInstanceAsync(transport, connector);

        var (vm, _) = BuildAsync(instance, transport);
        var tab = await ServerTabAsync(vm);

        Assert.Equal("Armor HAW", Assert.Single(tab.Loaded).Name);
        Assert.Equal("", tab.Status);
    }
}
