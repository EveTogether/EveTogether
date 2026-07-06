using System.Threading.Tasks;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>
/// the Auto Apply / Auto Invite toggles are persisted server-side (storage-role only — no ESI call). Owner-only;
/// both flags are written every call so toggling one never clears the other. Backed by a real <see cref="FleetRepository"/>
/// over throwaway SQLite.
/// </summary>
public class SetFleetEsiAutomationTests
{
    private readonly SqliteServerDbContextFactory _factory = new();

    private async Task<(FleetRepository Repo, long FleetId)> FleetAsync(int owner, System.Threading.CancellationToken ct)
    {
        var repo = new FleetRepository(_factory);
        var fleetId = await repo.AddAsync(new FleetEntity { Name = "Home Defense", CreatorCharacterId = owner, State = FleetState.Active }, ct);
        return (repo, fleetId);
    }

    [Fact]
    public async Task Set_AsOwner_PersistsBothFlags()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId) = await FleetAsync(owner: 100, ct);
        var handler = new SetFleetEsiAutomationCommandHandler(repo);

        var result = await handler.Handle(
            new SetFleetEsiAutomationCommand(fleetId, ActingCharacterId: 100, AutoApplyStructure: true, AutoInviteMembers: true), ct);

        Assert.True(result.IsSuccess);
        var reloaded = await repo.GetAsync(fleetId, ct);
        Assert.True(reloaded!.EsiAutoApplyStructure);
        Assert.True(reloaded.EsiAutoInviteMembers);
    }

    [Fact]
    public async Task Set_TogglingOne_LeavesTheOtherUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId) = await FleetAsync(owner: 100, ct);
        var handler = new SetFleetEsiAutomationCommandHandler(repo);

        await handler.Handle(new SetFleetEsiAutomationCommand(fleetId, 100, AutoApplyStructure: true, AutoInviteMembers: true), ct);
        // Turn Auto Apply off but keep Auto Invite on — the caller passes the full desired state, so both are written.
        await handler.Handle(new SetFleetEsiAutomationCommand(fleetId, 100, AutoApplyStructure: false, AutoInviteMembers: true), ct);

        var reloaded = await repo.GetAsync(fleetId, ct);
        Assert.False(reloaded!.EsiAutoApplyStructure);
        Assert.True(reloaded.EsiAutoInviteMembers);
    }

    [Fact]
    public async Task Set_ByNonOwner_IsRejected_AndKeepsTheFlags()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId) = await FleetAsync(owner: 100, ct);
        var handler = new SetFleetEsiAutomationCommandHandler(repo);

        var result = await handler.Handle(
            new SetFleetEsiAutomationCommand(fleetId, ActingCharacterId: 200, AutoApplyStructure: true, AutoInviteMembers: true), ct);

        Assert.False(result.IsSuccess);
        var reloaded = await repo.GetAsync(fleetId, ct);
        Assert.False(reloaded!.EsiAutoApplyStructure);
        Assert.False(reloaded.EsiAutoInviteMembers);
    }

    [Fact]
    public async Task Set_UnknownFleet_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new SetFleetEsiAutomationCommandHandler(new FleetRepository(_factory));

        var result = await handler.Handle(
            new SetFleetEsiAutomationCommand(FleetId: 12345, ActingCharacterId: 100, AutoApplyStructure: true, AutoInviteMembers: true), ct);

        Assert.False(result.IsSuccess);
    }
}
