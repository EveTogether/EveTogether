using System.Threading.Tasks;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>
/// of the ESI fleet-coupling: the owner links an internal fleet to a live in-game fleet_id. Sets the
/// ESI-parity naden + flips the fleet to <see cref="EsiFleetSyncState.Linked"/> (Q4: one internal ↔ one in-game).
/// Owner-only. Backed by a real <see cref="FleetRepository"/> over throwaway SQLite.
/// </summary>
public class CoupleFleetToEsiTests
{
    private readonly SqliteServerDbContextFactory _factory = new();

    [Fact]
    public async Task Couple_AsOwner_LinksFleetToEsiFleet()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = new FleetRepository(_factory);
        var fleetId = await repo.AddAsync(new FleetEntity { Name = "Home Defense", CreatorCharacterId = 100, State = FleetState.Active }, ct);
        var handler = new CoupleFleetToEsiCommandHandler(repo);

        var result = await handler.Handle(
            new CoupleFleetToEsiCommand(fleetId, EsiFleetId: 999, EsiFleetBossId: 100, ActingCharacterId: 100), ct);

        Assert.True(result.IsSuccess);
        var reloaded = await repo.GetAsync(fleetId, ct);
        Assert.Equal(999, reloaded!.EsiFleetId);
        Assert.Equal(100, reloaded.EsiFleetBossId);
        Assert.Equal(EsiFleetSyncState.Linked, reloaded.EsiSyncState);
    }

    [Fact]
    public async Task Couple_ByNonOwner_IsRejected_AndLeavesFleetUnlinked()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = new FleetRepository(_factory);
        var fleetId = await repo.AddAsync(new FleetEntity { Name = "Home Defense", CreatorCharacterId = 100, State = FleetState.Active }, ct);
        var handler = new CoupleFleetToEsiCommandHandler(repo);

        var result = await handler.Handle(new CoupleFleetToEsiCommand(fleetId, 999, 100, ActingCharacterId: 200), ct);

        Assert.False(result.IsSuccess);
        var reloaded = await repo.GetAsync(fleetId, ct);
        Assert.Null(reloaded!.EsiFleetId);
        Assert.Equal(EsiFleetSyncState.NotLinked, reloaded.EsiSyncState);
    }

    [Fact]
    public async Task Couple_UnknownFleet_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new CoupleFleetToEsiCommandHandler(new FleetRepository(_factory));

        var result = await handler.Handle(new CoupleFleetToEsiCommand(FleetId: 12345, 999, 100, 100), ct);

        Assert.False(result.IsSuccess);
    }
}
