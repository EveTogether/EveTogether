using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// fleet-overview per-character actions (multi-box): a fleet I own can still take another of my alts (JOIN /
/// REQUEST stay offered even on my own / already-joined fleet), and each of my non-owner characters in a fleet gets a
/// per-leaf LEAVE so an alt can leave while the owner stays. Drives the real <see cref="FleetsViewModel"/> over the
/// faked transport.
/// </summary>
public class FleetCardAltActionsTests
{
    private const string Server = "srv:7443";
    private const int Me = 100;       // owner
    private const int Catbank = 200;  // my alt

    private static FleetInfo Fleet(long id, string name, int owner, FleetVisibility visibility) =>
        new(id, name, null, visibility, FleetState.Active, owner, null, null,
            DateTimeOffset.UnixEpoch, FleetActivation.Forming, null);

    private static FleetMemberInfo Member(long memberId, int characterId, FleetRole role) =>
        new(memberId, characterId, 0, 0, role, false, null, null);

    private static async Task<FleetsViewModel> LoadedVmAsync(TestClientInstance instance, params int[] coupled)
    {
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        foreach (var id in coupled)
            await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", id == Me ? "RaymondKrah" : "Catbank", id));
        var vm = new FleetsViewModel(instance.Services);
        for (var i = 0; i < 100 && vm.ServerGroups.Count == 0; i++)
            await Task.Delay(50);
        return vm;
    }

    [AvaloniaFact]
    public async Task OwnedFleet_NonOwnerAlt_GetsLeaveLeaf_ButOwnerCharacterDoesNot()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Fleet(11, "Test", Me, FleetVisibility.Public)];
        transport.MembersByFleet[11] = [Member(1, Me, FleetRole.FleetCommander), Member(2, Catbank, FleetRole.SquadMember)];

        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        var vm = await LoadedVmAsync(instance, Me, Catbank);

        var card = Assert.Single(Assert.Single(vm.ServerGroups).Fleets);
        Assert.True(card.IsMine);

        var ownerLeaf = Assert.Single(card.Members, m => m.CharacterId == Me);
        Assert.False(ownerLeaf.CanLeave);          // the owner disbands/transfers, never leaves
        Assert.Null(ownerLeaf.LeaveCommand);

        var altLeaf = Assert.Single(card.Members, m => m.CharacterId == Catbank);
        Assert.True(altLeaf.CanLeave);
        Assert.NotNull(altLeaf.LeaveCommand);

        await altLeaf.LeaveCommand!.ExecuteAsync(null);
        Assert.Contains((Server, 11L, Catbank), transport.LeaveCalls);
        Assert.DoesNotContain(transport.LeaveCalls, c => c.ActingCharacterId == Me);
    }

    [AvaloniaFact]
    public async Task OwnedPublicFleet_WithAFreeAlt_ShowsEnabledJoin()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Fleet(11, "Test Join", Me, FleetVisibility.Public)];
        transport.MembersByFleet[11] = [Member(1, Me, FleetRole.FleetCommander)];   // only the owner is in; Catbank is free

        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        var vm = await LoadedVmAsync(instance, Me, Catbank);

        var card = Assert.Single(Assert.Single(vm.ServerGroups).Fleets);
        Assert.True(card.IsMine);
        Assert.True(card.ShowJoin);          // public → JOIN, even though it's my own fleet
        Assert.False(card.ShowRequest);
        Assert.True(card.JoinEnabled);       // Catbank is free to join
    }

    [AvaloniaFact]
    public async Task OwnedInviteOnlyFleet_WithAFreeAlt_ShowsRequest()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Fleet(11, "Test Invite", Me, FleetVisibility.InviteOnly)];
        transport.MembersByFleet[11] = [Member(1, Me, FleetRole.FleetCommander)];

        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        var vm = await LoadedVmAsync(instance, Me, Catbank);

        var card = Assert.Single(Assert.Single(vm.ServerGroups).Fleets);
        Assert.True(card.ShowRequest);       // invite-only → REQUEST
        Assert.False(card.ShowJoin);
        Assert.True(card.JoinEnabled);
    }

    [AvaloniaFact]
    public async Task OwnedPublicFleet_EveryAltAlreadyIn_KeepsJoinVisibleButDisabled()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Fleet(11, "Test", Me, FleetVisibility.Public)];
        transport.MembersByFleet[11] = [Member(1, Me, FleetRole.FleetCommander), Member(2, Catbank, FleetRole.SquadMember)];

        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        var vm = await LoadedVmAsync(instance, Me, Catbank);

        var card = Assert.Single(Assert.Single(vm.ServerGroups).Fleets);
        Assert.True(card.ShowJoin);          // visible so "all in" reads as greyed-out, not missing
        Assert.False(card.JoinEnabled);      // no character free to join
    }
}
