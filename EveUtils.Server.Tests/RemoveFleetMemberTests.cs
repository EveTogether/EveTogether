using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>
/// Who may take a pilot off a fleet's roster. ET-44 puts this action behind a right-click on every member list, so
/// the rule has to hold where it actually matters — here, against the acting character the gRPC layer takes from the
/// authenticated session, not in whichever menu happened to offer it. The owner may remove anyone, a member may only
/// remove themselves, and the creator is removable by nobody until ownership has moved. Backed by a real
/// <see cref="FleetRepository"/> over throwaway SQLite.
/// </summary>
public class RemoveFleetMemberTests
{
    private const int Owner = 100;
    private const int Member = 200;
    private const int Outsider = 300;

    private readonly SqliteServerDbContextFactory _factory = new();

    private async Task<(FleetRepository Repo, long FleetId, long OwnerMemberId, long MemberId)> FleetAsync(CancellationToken ct)
    {
        var repo = new FleetRepository(_factory);
        var fleetId = await repo.AddAsync(
            new FleetEntity { Name = "Home Defense", CreatorCharacterId = Owner, State = FleetState.Active }, ct);

        var ownerMemberId = await repo.AddMemberAsync(
            new FleetMember { FleetId = fleetId, CharacterId = Owner, Role = FleetRole.FleetCommander, WingId = -1, SquadId = -1 }, ct);
        var memberId = await repo.AddMemberAsync(
            new FleetMember { FleetId = fleetId, CharacterId = Member, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1 }, ct);

        return (repo, fleetId, ownerMemberId, memberId);
    }

    [Fact]
    public async Task Remove_AsOwner_TakesTheMemberOffTheRoster()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId, _, memberId) = await FleetAsync(ct);
        var handler = new RemoveFleetMemberCommandHandler(repo);

        var result = await handler.Handle(new RemoveFleetMemberCommand(memberId, ActingCharacterId: Owner), ct);

        Assert.True(result.IsSuccess);
        Assert.Null(await repo.GetMemberAsync(memberId, ct));
        Assert.Single(await repo.ListMembersAsync(fleetId, ct));
    }

    /// <summary>The whole reason the check cannot live in the client: hiding the menu item is not a permission.</summary>
    [Fact]
    public async Task Remove_ByAnotherMember_IsRejected_AndKeepsThemOnTheRoster()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId, ownerMemberId, _) = await FleetAsync(ct);
        var handler = new RemoveFleetMemberCommandHandler(repo);

        // The ordinary member tries to remove someone who is not them.
        var result = await handler.Handle(new RemoveFleetMemberCommand(ownerMemberId, ActingCharacterId: Member), ct);

        Assert.False(result.IsSuccess);
        Assert.NotNull(await repo.GetMemberAsync(ownerMemberId, ct));
        Assert.Equal(2, (await repo.ListMembersAsync(fleetId, ct)).Count);
    }

    [Fact]
    public async Task Remove_ByAnOutsider_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, _, _, memberId) = await FleetAsync(ct);
        var handler = new RemoveFleetMemberCommandHandler(repo);

        var result = await handler.Handle(new RemoveFleetMemberCommand(memberId, ActingCharacterId: Outsider), ct);

        Assert.False(result.IsSuccess);
        Assert.NotNull(await repo.GetMemberAsync(memberId, ct));
    }

    // Leaving is the same command with yourself as the target, so it stays allowed for an ordinary member.
    [Fact]
    public async Task Remove_OfThemselves_IsAllowedForAnOrdinaryMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, _, _, memberId) = await FleetAsync(ct);
        var handler = new RemoveFleetMemberCommandHandler(repo);

        var result = await handler.Handle(new RemoveFleetMemberCommand(memberId, ActingCharacterId: Member), ct);

        Assert.True(result.IsSuccess);
        Assert.Null(await repo.GetMemberAsync(memberId, ct));
    }

    // A fleet always has an owner: not even the owner may remove themselves before handing ownership on. This is
    // why the shared member menu offers no removal on the creator's own row.
    [Fact]
    public async Task Remove_OfTheCreator_IsRejected_EvenByTheCreator()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, _, ownerMemberId, _) = await FleetAsync(ct);
        var handler = new RemoveFleetMemberCommandHandler(repo);

        var result = await handler.Handle(new RemoveFleetMemberCommand(ownerMemberId, ActingCharacterId: Owner), ct);

        Assert.False(result.IsSuccess);
        Assert.NotNull(await repo.GetMemberAsync(ownerMemberId, ct));
    }

    [Fact]
    public async Task Remove_OfAnUnknownMember_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, _, _, _) = await FleetAsync(ct);
        var handler = new RemoveFleetMemberCommandHandler(repo);

        var result = await handler.Handle(new RemoveFleetMemberCommand(MemberId: 999_999, ActingCharacterId: Owner), ct);

        Assert.False(result.IsSuccess);
    }

    // A roster change resets the inactivity grace, so an emptying fleet is not cleaned up mid-operation.
    [Fact]
    public async Task Remove_BumpsTheFleetActivityClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId, _, memberId) = await FleetAsync(ct);
        var before = (await repo.GetAsync(fleetId, ct))!.LastActivityAt;
        var handler = new RemoveFleetMemberCommandHandler(repo);

        await handler.Handle(new RemoveFleetMemberCommand(memberId, ActingCharacterId: Owner), ct);

        Assert.True((await repo.GetAsync(fleetId, ct))!.LastActivityAt > before);
    }
}
