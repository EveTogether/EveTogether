using System.Threading.Tasks;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>
/// item 5: the inverse of <see cref="CoupleFleetToEsiTests"/>. When the in-game fleet is gone the stored link is
/// cleared (EsiFleetId/BossId wiped, state back to <see cref="EsiFleetSyncState.NotLinked"/>) so no client keeps polling
/// ESI for a dead fleet. Owner-only; storage-role only (the handler makes no ESI call). Backed by a real
/// <see cref="FleetRepository"/> over throwaway SQLite.
/// </summary>
public class UncoupleFleetFromEsiTests
{
    private readonly SqliteServerDbContextFactory _factory = new();

    private async Task<(FleetRepository Repo, long FleetId)> CoupledFleetAsync(int owner, System.Threading.CancellationToken ct)
    {
        var repo = new FleetRepository(_factory);
        var fleetId = await repo.AddAsync(new FleetEntity { Name = "Home Defense", CreatorCharacterId = owner, State = FleetState.Active }, ct);
        var fleet = (await repo.GetAsync(fleetId, ct))!;
        fleet.EsiFleetId = 999;
        fleet.EsiFleetBossId = owner;
        fleet.EsiSyncState = EsiFleetSyncState.Linked;
        await repo.UpdateAsync(fleet, ct);
        return (repo, fleetId);
    }

    [Fact]
    public async Task Uncouple_AsOwner_ClearsTheStoredEsiLink()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId) = await CoupledFleetAsync(owner: 100, ct);
        var handler = new UncoupleFleetFromEsiCommandHandler(repo);

        var result = await handler.Handle(new UncoupleFleetFromEsiCommand(fleetId, ActingCharacterId: 100), ct);

        Assert.True(result.IsSuccess);
        var reloaded = await repo.GetAsync(fleetId, ct);
        Assert.Null(reloaded!.EsiFleetId);
        Assert.Null(reloaded.EsiFleetBossId);
        Assert.Equal(EsiFleetSyncState.NotLinked, reloaded.EsiSyncState);
    }

    [Fact]
    public async Task Uncouple_ByNonOwner_IsRejected_AndKeepsTheLink()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId) = await CoupledFleetAsync(owner: 100, ct);
        var handler = new UncoupleFleetFromEsiCommandHandler(repo);

        var result = await handler.Handle(new UncoupleFleetFromEsiCommand(fleetId, ActingCharacterId: 200), ct);

        Assert.False(result.IsSuccess);
        var reloaded = await repo.GetAsync(fleetId, ct);
        Assert.Equal(999, reloaded!.EsiFleetId);
        Assert.Equal(EsiFleetSyncState.Linked, reloaded.EsiSyncState);
    }

    [Fact]
    public async Task Uncouple_AlreadyUnlinked_IsIdempotentSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = new FleetRepository(_factory);
        var fleetId = await repo.AddAsync(new FleetEntity { Name = "Home Defense", CreatorCharacterId = 100, State = FleetState.Active }, ct);
        var handler = new UncoupleFleetFromEsiCommandHandler(repo);

        var result = await handler.Handle(new UncoupleFleetFromEsiCommand(fleetId, ActingCharacterId: 100), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(EsiFleetSyncState.NotLinked, (await repo.GetAsync(fleetId, ct))!.EsiSyncState);
    }

    [Fact]
    public async Task Uncouple_UnknownFleet_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new UncoupleFleetFromEsiCommandHandler(new FleetRepository(_factory));

        var result = await handler.Handle(new UncoupleFleetFromEsiCommand(FleetId: 12345, ActingCharacterId: 100), ct);

        Assert.False(result.IsSuccess);
    }
}
