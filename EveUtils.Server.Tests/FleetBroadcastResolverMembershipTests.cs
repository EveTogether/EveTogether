using EveUtils.Server.Grpc;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>
/// The send-side membership gate (A2): <see cref="FleetBroadcastResolver.IsMemberAsync"/> must let a roster member of
/// the fleet through and reject a non-member, so a stranger cannot reroute events into a fleet's broadcast stream.
/// Backed by a real <see cref="FleetRepository"/> over SQLite.
/// </summary>
public sealed class FleetBroadcastResolverMembershipTests : IDisposable
{
    private const int Member = 95100001;
    private const int Stranger = 95100099;

    private readonly SqliteServerDbContextFactory _factory = new();

    [Fact]
    public async Task IsMemberAsync_RosterMember_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (resolver, fleetId) = await SetupFleetWithMemberAsync(ct);

        Assert.True(await resolver.IsMemberAsync(fleetId, Member, ct));
    }

    [Fact]
    public async Task IsMemberAsync_NonMember_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var (resolver, fleetId) = await SetupFleetWithMemberAsync(ct);

        Assert.False(await resolver.IsMemberAsync(fleetId, Stranger, ct));
    }

    private async Task<(FleetBroadcastResolver Resolver, long FleetId)> SetupFleetWithMemberAsync(CancellationToken cancellationToken)
    {
        var repository = new FleetRepository(_factory);
        var now = DateTimeOffset.UtcNow;
        var fleetId = await repository.AddAsync(new FleetEntity
        {
            Name = "Home Defence",
            CreatorCharacterId = Member,
            State = FleetState.Active,
            Activation = FleetActivation.Active,
            CreatedAt = now,
            LastActivityAt = now
        }, cancellationToken);
        await repository.AddMemberAsync(new FleetMember
        {
            FleetId = fleetId,
            CharacterId = Member,
            Role = FleetRole.FleetCommander,
            JoinTime = now
        }, cancellationToken);

        var resolver = new FleetBroadcastResolver(repository, new ConnectedClients());
        return (resolver, fleetId);
    }

    public void Dispose() => _factory.Dispose();
}
