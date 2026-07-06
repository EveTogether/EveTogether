using System;
using System.Collections.Generic;
using System.Linq;
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
/// Regression guard for the RequestToJoin acting-character picker (commit 55bb367). The discover browser is
/// server-wide, so requesting to join an invite-only fleet must <em>pick</em> which coupled character requests —
/// not silently default to the most-recent session (the original bug). Drives the real <see cref="FleetsViewModel"/>
/// over a faked <see cref="IFleetTransportClient"/> (no server) and asserts the picked character is the one the
/// request reaches the server as.
/// </summary>
public class FleetRequestPickerTests
{
    private const string Server = "localhost:7443";
    private const long FleetId = 500;
    private const int Owner = 999;       // owns the fleet → never a candidate to request to join it
    private const int Lionear = 100;
    private const int Maricadie = 200;   // seeded last → the most-recent session (the old silent default)

    [AvaloniaFact]
    public async Task Request_WithMultipleCoupledCharacters_SendsAsThePickedCharacter_NotTheMostRecent()
    {
        var transport = new RecordingFleetTransportClient();
        var dialogs = new RecordingDialogService { OnPickCharacter = (_, _) => Task.FromResult<int?>(Lionear) };

        using var instance = CreateInstance(transport, dialogs);
        var vm = await SeedAndBuildAsync(instance);
        await vm.RequestCommand.ExecuteAsync(InviteOnlyRow());

        // The picker was consulted with both my characters, the owner excluded.
        Assert.NotNull(dialogs.LastOptions);
        Assert.Equal(new[] { Lionear, Maricadie }, dialogs.LastOptions!.Select(o => o.CharacterId).OrderBy(id => id));
        Assert.DoesNotContain(dialogs.LastOptions!, o => o.CharacterId == Owner);

        // The request reached the server as the PICKED character — not 0/most-recent (which would be Maricadie).
        var call = Assert.Single(transport.RequestToJoinCalls);
        Assert.Equal(Server, call.ServerAddress);
        Assert.Equal(FleetId, call.FleetId);
        Assert.Equal(Lionear, call.ActingCharacterId);
    }

    [AvaloniaFact]
    public async Task Request_ExcludesCharactersAlreadyInTheFleet_AutoPicksTheRemainingOne()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MembersByFleet[FleetId] = [new FleetMemberInfo(1, Lionear, -1, -1, FleetRole.SquadMember, false)];
        // Lionear is already a member and Owner owns it → only Maricadie is eligible → auto-pick, no dialog.
        var dialogs = new RecordingDialogService
        {
            OnPickCharacter = (_, _) => throw new InvalidOperationException("the picker must not show for a single eligible character"),
        };

        using var instance = CreateInstance(transport, dialogs);
        var vm = await SeedAndBuildAsync(instance);
        await vm.RequestCommand.ExecuteAsync(InviteOnlyRow());

        Assert.Null(dialogs.LastOptions); // dialog never consulted
        var call = Assert.Single(transport.RequestToJoinCalls);
        Assert.Equal(Maricadie, call.ActingCharacterId);
    }

    [AvaloniaFact]
    public async Task Request_WhenPickerCancelled_SendsNothing()
    {
        var transport = new RecordingFleetTransportClient();
        var dialogs = new RecordingDialogService { OnPickCharacter = (_, _) => Task.FromResult<int?>(null) };

        using var instance = CreateInstance(transport, dialogs);
        var vm = await SeedAndBuildAsync(instance);
        await vm.RequestCommand.ExecuteAsync(InviteOnlyRow());

        Assert.Empty(transport.RequestToJoinCalls);
    }

    private static TestClientInstance CreateInstance(RecordingFleetTransportClient transport, RecordingDialogService dialogs) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IDialogService>(dialogs);
        });

    private static async Task<FleetsViewModel> SeedAndBuildAsync(TestClientInstance instance)
    {
        // Two characters coupled to one server; Maricadie saved last → the most-recent session.
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync(Server, new ClientSessionTokens("token", "refresh", "Lionear", Lionear));
        await sessions.SaveAsync(Server, new ClientSessionTokens("token", "refresh", "Maricadie", Maricadie));

        var vm = new FleetsViewModel(instance.Services);
        for (var i = 0; i < 100 && !vm.CanInteract; i++)
            await Task.Delay(50);
        Assert.True(vm.CanInteract, "FleetsViewModel never resolved the coupled server.");
        return vm;
    }

    private static FleetViewModel InviteOnlyRow()
    {
        var fleet = new FleetInfo(FleetId, "Hidden Fleet", null, FleetVisibility.InviteOnly, FleetState.Active,
            Owner, null, null, DateTimeOffset.UnixEpoch, FleetActivation.Forming);
        // A browser row carries the server it lives on — the action targets row.ServerAddress.
        return new FleetViewModel(fleet, actingCharacterId: 0, serverAddress: Server, serverName: "Test Server");
    }
}
