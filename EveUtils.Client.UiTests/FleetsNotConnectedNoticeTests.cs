using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-13: a known character without a session on a server is never asked for its fleets there, and the list used to
/// stay silent about it — a shorter list, indistinguishable from "no such fleet".
/// </summary>
public class FleetsNotConnectedNoticeTests
{
    private const string Server = "srv:7443";
    private const int Connected = 100;
    private const int NotConnected = 200;

    [AvaloniaFact]
    public async Task KnownCharacterWithoutSession_IsNamedInOneNoticeForTheServer()
    {
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(new RecordingFleetTransportClient());
            services.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        await _SeedAsync(instance.Services, connectedToServer: [Connected]);

        var vm = new FleetsViewModel(instance.Services);
        await vm.RefreshCommand.ExecuteAsync(null);

        string notice = Assert.Single(vm.NotConnectedNotices);
        Assert.Equal("Catbank is not connected to srv:7443 — its fleets are not shown here", notice);
        Assert.True(vm.HasNotConnectedNotices);
    }

    [AvaloniaFact]
    public async Task ConnectingTheMissingCharacter_RemovesTheNotice()
    {
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(new RecordingFleetTransportClient());
            services.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        await _SeedAsync(instance.Services, connectedToServer: [Connected]);

        var vm = new FleetsViewModel(instance.Services);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.HasNotConnectedNotices);

        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", "Catbank", NotConnected));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.NotConnectedNotices);
        Assert.False(vm.HasNotConnectedNotices);
    }

    private static async Task _SeedAsync(IServiceProvider services, int[] connectedToServer)
    {
        var registry = services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Lionear", Connected));
        await registry.AddOrUpdateAsync(new Character("Catbank", NotConnected));

        var sessions = services.GetRequiredService<IClientSessionStore>();
        foreach (int id in connectedToServer)
            await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", "Lionear", id));
    }
}
