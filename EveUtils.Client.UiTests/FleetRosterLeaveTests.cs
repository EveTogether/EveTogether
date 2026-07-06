using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Modules.Fleet.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// a non-owner member can leave a fleet from the roster window (self-removal, distinct from the owner-only
/// remove). Multi-box aware — with several of my own characters in the fleet a picker asks which to pull out. The
/// owner's own character is never a leave candidate (it disbands/transfers).
/// </summary>
public class FleetRosterLeaveTests
{
    private const int Owner = 95000040;
    private const int Me = 96000040;
    private const int Alt = 96000041;

    private static FleetInfo Fleet(int owner) =>
        new(8, "HoS", null, FleetVisibility.Public, FleetState.Active, owner,
            null, null, DateTimeOffset.UtcNow, FleetActivation.Active);

    private static FleetMemberInfo Member(long id, int characterId, FleetRole role) =>
        new(id, characterId, -1, -1, role, false, null, null);

    private static async Task WaitLoadedAsync(FleetRosterViewModel roster)
    {
        for (var i = 0; i < 100 && roster.Entries.Count == 0; i++) await Task.Delay(50);
    }

    [AvaloniaFact]
    public async Task NonOwnerMember_LeavesFromTheRoster()
    {
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(dialogs));
        var fake = new FakeFleetClient
        {
            Members = [Member(5, Me, FleetRole.SquadMember)],
            Connected = [new ConnectedCharacterInfo(Me, "Me")],
            Fleet = Fleet(Owner)
        };

        using var roster = new FleetRosterViewModel(instance.Services, fake, fake.Fleet!, isOwner: false, actingCharacterId: Me);
        await WaitLoadedAsync(roster);

        Assert.True(roster.CanLeaveFleet);
        await roster.LeaveFleetCommand.ExecuteAsync(null);
        Assert.Equal((8L, Me), Assert.Single(fake.LeaveFleetCalls));
    }

    [AvaloniaFact]
    public async Task MultipleOwnCharacters_PicksWhichToLeave()
    {
        // The picker chooses the second candidate → only that character leaves, the other stays.
        var dialogs = new RecordingDialogService
        {
            OnPickCharacters = (_, opts) => Task.FromResult<IReadOnlyList<int>?>([opts[1].CharacterId])
        };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(dialogs));
        var fake = new FakeFleetClient
        {
            Members = [Member(5, Me, FleetRole.SquadMember), Member(6, Alt, FleetRole.SquadMember)],
            Connected = [new ConnectedCharacterInfo(Me, "Me"), new ConnectedCharacterInfo(Alt, "Alt")],
            Fleet = Fleet(Owner)
        };

        using var roster = new FleetRosterViewModel(instance.Services, fake, fake.Fleet!, isOwner: false, actingCharacterId: Me);
        await WaitLoadedAsync(roster);

        Assert.True(roster.CanLeaveFleet);
        await roster.LeaveFleetCommand.ExecuteAsync(null);

        var call = Assert.Single(fake.LeaveFleetCalls);
        Assert.Equal((8L, Alt), call);   // only the picked character, not both
    }

    [AvaloniaFact]
    public async Task Owner_OwnCharacter_IsNeverALeaveCandidate()
    {
        using var instance = TestClientInstance.Create();
        var fake = new FakeFleetClient
        {
            Members = [Member(5, Owner, FleetRole.FleetCommander)],
            Connected = [new ConnectedCharacterInfo(Owner, "Owner")],
            Fleet = Fleet(Owner)
        };

        using var roster = new FleetRosterViewModel(instance.Services, fake, fake.Fleet!, isOwner: true, actingCharacterId: Owner);
        await WaitLoadedAsync(roster);

        Assert.False(roster.CanLeaveFleet);
    }

    [AvaloniaFact]
    public async Task NonMemberViewer_GetsNoLeave()
    {
        using var instance = TestClientInstance.Create();
        var fake = new FakeFleetClient
        {
            Members = [Member(5, Owner, FleetRole.FleetCommander)],   // I (Me) am connected but not on the roster
            Connected = [new ConnectedCharacterInfo(Me, "Me")],
            Fleet = Fleet(Owner)
        };

        using var roster = new FleetRosterViewModel(instance.Services, fake, fake.Fleet!, isOwner: false, actingCharacterId: Me);
        await WaitLoadedAsync(roster);

        Assert.False(roster.CanLeaveFleet);
    }
}
