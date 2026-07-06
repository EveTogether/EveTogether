using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Modules.Fleet.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The roster must show a name for EVERY row — including a pending invitee who is not in the live connected set.
/// Earlier only accepted members were name-resolved, so a pending external pilot rendered as "Char &lt;id&gt;".
/// Now invitees (and join-requesters) are resolved through the same public-ESI lookup.
/// </summary>
public class FleetRosterNameResolutionTests
{
    private const int Owner = 95000001;
    private const int Invitee = 883434905;

    [AvaloniaFact]
    public async Task PendingInvitee_ShowsResolvedName_NotCharId()
    {
        var lookup = new FakeExternalLookup { [Invitee] = "Lyra Custos" };
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IExternalCharacterLookup>(lookup));

        var client = new FakeFleetClient
        {
            Members = [new FleetMemberInfo(1, Owner, -1, -1, FleetRole.FleetCommander, false)],
            Invites = [new FleetInviteInfo(10, 100, Owner, Invitee, FleetRole.SquadMember, FleetInviteStatus.Pending)],
            Connected = [new ConnectedCharacterInfo(Owner, "Jithran")], // the invitee is NOT connected → must be looked up
        };

        var fleet = new FleetInfo(100, "Test 3", null, FleetVisibility.InviteOnly, FleetState.Active, Owner,
            null, null, DateTimeOffset.UnixEpoch, FleetActivation.Forming);
        var roster = new FleetRosterViewModel(instance.Services, client, fleet, isOwner: true, Owner);

        for (var i = 0; i < 100 && roster.Entries.Count < 2; i++)
            await Task.Delay(50);

        var pending = Assert.Single(roster.Entries, e => e.Badge.Contains("pending"));
        Assert.Equal("Lyra Custos", pending.Name);
        Assert.DoesNotContain("Char ", pending.Name);
    }
}
