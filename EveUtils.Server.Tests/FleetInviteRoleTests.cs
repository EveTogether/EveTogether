using System;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

public class FleetInviteRoleTests
{
    private readonly SqliteServerDbContextFactory _factory = new();

    /// <summary>ET-23: an accepted Wing Commander invite seats the pilot on the wing (squad -1), not in a squad.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedWingCommanderInvite_LandsOnTheWing_NotInASquad(bool inviteNamesASquad)
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = new FleetRepository(_factory);
        var fleetId = await repo.AddAsync(new FleetEntity
        {
            Name = "WC run", CreatorCharacterId = 1, State = FleetState.Active, Activation = FleetActivation.Active,
        }, ct);
        var wingId = await repo.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "Wing 1" }, ct);
        var squadId = await repo.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Squad 1" }, ct);
        var inviteId = await repo.AddInviteAsync(new FleetInvite
        {
            FleetId = fleetId, InviterCharacterId = 1, InviteeCharacterId = 2, Role = FleetRole.WingCommander,
            WingId = inviteNamesASquad ? wingId : null, SquadId = inviteNamesASquad ? squadId : null,
            Status = FleetInviteStatus.Pending, CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        var accepted = await new RespondToFleetInviteCommandHandler(repo, new InProcessEventBus())
            .Handle(new RespondToFleetInviteCommand(inviteId, Accept: true, ActingCharacterId: 2), ct);

        Assert.True(accepted.IsSuccess);
        var member = (await repo.ListMembersAsync(fleetId, ct)).Single(m => m.CharacterId == 2);
        Assert.Equal(FleetRole.WingCommander, member.Role);
        Assert.Equal(wingId, member.WingId);
        Assert.Equal(-1, member.SquadId);
    }
}
