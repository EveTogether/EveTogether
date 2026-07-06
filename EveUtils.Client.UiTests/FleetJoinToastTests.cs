using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Transport;
using EveUtils.Shared.Modules.Fleet.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Guards the "always confirm a join with a toast" behaviour. Both join paths converge on
/// <see cref="FleetsViewModel.JoinCommand"/>: the multi-character path shows a picker, the single-character path
/// auto-picks and used to enter silently (only the easily-missed status bar changed). Drives the real
/// <see cref="FleetsViewModel"/> over a faked transport + dialog + a <see cref="RecordingToastService"/>.
/// </summary>
public class FleetJoinToastTests
{
    private const string Server = "localhost:7443";
    private const long FleetId = 600;
    private const int Owner = 999;
    private const int Lionear = 100;
    private const int Maricadie = 200;

    [AvaloniaFact]
    public async Task Join_WithSingleEligibleCharacter_ShowsToast_WithFleetAndCharacter()
    {
        var transport = new RecordingFleetTransportClient();
        // A single coupled character auto-joins with no picker — this is the path that used to be silent.
        var dialogs = new RecordingDialogService
        {
            OnPickCharacter = (_, _) => throw new InvalidOperationException("the picker must not show for a single eligible character"),
        };
        var toasts = new RecordingToastService();

        using var instance = CreateInstance(transport, dialogs, toasts);
        var vm = await SeedAndBuildAsync(instance, ("Lionear", Lionear));
        await vm.JoinCommand.ExecuteAsync(PublicRow());

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("Joined 'Home Defense Fleet'", toast.Title);
        Assert.Equal("as Lionear", toast.Message);
    }

    [AvaloniaFact]
    public async Task Join_WithMultipleCharacters_ShowsToast_ForThePickedCharacter()
    {
        var transport = new RecordingFleetTransportClient();
        var dialogs = new RecordingDialogService { OnPickCharacters = (_, _) => Task.FromResult<IReadOnlyList<int>?>([Maricadie]) };
        var toasts = new RecordingToastService();

        using var instance = CreateInstance(transport, dialogs, toasts);
        var vm = await SeedAndBuildAsync(instance, ("Lionear", Lionear), ("Maricadie", Maricadie));
        await vm.JoinCommand.ExecuteAsync(PublicRow());

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("Joined 'Home Defense Fleet'", toast.Title);
        Assert.Equal("as Maricadie", toast.Message); // the PICKED character, not the most-recent default
    }

    [AvaloniaFact]
    public async Task Join_WhenEveryConnectedCharacterIsAlreadyAMember_ShowsInfoToast_AndDoesNotJoin()
    {
        var transport = new RecordingFleetTransportClient();
        // The only character coupled to this server is already in the fleet → nothing can join; the user's other
        // (uncoupled) characters are invisible to this server, which is exactly what the toast must explain.
        transport.MembersByFleet[FleetId] = [new FleetMemberInfo(1, Lionear, -1, -1, FleetRole.SquadMember, false)];
        var dialogs = new RecordingDialogService
        {
            OnPickCharacter = (_, _) => throw new InvalidOperationException("the picker must not show when nothing can join"),
        };
        var toasts = new RecordingToastService();

        using var instance = CreateInstance(transport, dialogs, toasts);
        var vm = await SeedAndBuildAsync(instance, ("Lionear", Lionear));
        await vm.JoinCommand.ExecuteAsync(PublicRow());

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("Can't join", toast.Title);
        Assert.Equal(ToastKind.Information, toast.Kind); // informs, and the absence of a "Joined" toast proves no join
    }

    [AvaloniaFact]
    public async Task Join_WhenJoinFails_ShowsErrorToast_WithTheServerReason()
    {
        var transport = new RecordingFleetTransportClient { JoinResult = (false, "Server unreachable.") };
        var dialogs = new RecordingDialogService
        {
            OnPickCharacter = (_, _) => throw new InvalidOperationException("the picker must not show for a single eligible character"),
        };
        var toasts = new RecordingToastService();

        using var instance = CreateInstance(transport, dialogs, toasts);
        var vm = await SeedAndBuildAsync(instance, ("Lionear", Lionear));
        await vm.JoinCommand.ExecuteAsync(PublicRow());

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("Join failed", toast.Title);
        Assert.Equal("Server unreachable.", toast.Message);
        Assert.Equal(ToastKind.Error, toast.Kind);
    }

    [AvaloniaFact]
    public async Task Join_WhenPickerCancelled_ShowsNoToast()
    {
        var transport = new RecordingFleetTransportClient();
        var dialogs = new RecordingDialogService { OnPickCharacter = (_, _) => Task.FromResult<int?>(null) };
        var toasts = new RecordingToastService();

        using var instance = CreateInstance(transport, dialogs, toasts);
        var vm = await SeedAndBuildAsync(instance, ("Lionear", Lionear), ("Maricadie", Maricadie));
        await vm.JoinCommand.ExecuteAsync(PublicRow());

        Assert.Empty(toasts.Toasts);
    }

    private static TestClientInstance CreateInstance(
        RecordingFleetTransportClient transport, RecordingDialogService dialogs, RecordingToastService toasts) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IDialogService>(dialogs);
            services.AddSingleton<IToastService>(toasts);
        });

    private static async Task<FleetsViewModel> SeedAndBuildAsync(
        TestClientInstance instance, params (string Name, int Id)[] characters)
    {
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        foreach (var (name, id) in characters)
            await sessions.SaveAsync(Server, new ClientSessionTokens("token", "refresh", name, id));

        var vm = new FleetsViewModel(instance.Services);
        for (var i = 0; i < 100 && !vm.CanInteract; i++)
            await Task.Delay(50);
        Assert.True(vm.CanInteract, "FleetsViewModel never resolved the coupled server.");
        return vm;
    }

    private static FleetViewModel PublicRow()
    {
        var fleet = new FleetInfo(FleetId, "Home Defense Fleet", null, FleetVisibility.Public, FleetState.Active,
            Owner, null, null, DateTimeOffset.UnixEpoch, FleetActivation.Forming);
        return new FleetViewModel(fleet, actingCharacterId: 0, serverAddress: Server, serverName: "Test Server");
    }
}
