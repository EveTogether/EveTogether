using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Queries;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>
/// The collision at the start, and the way out of it (ET-168). Two rules carry the whole feature and neither is
/// visible from a screen: <b>the commander asks and never moves anyone</b>, and <b>the member's switch is one act</b>
/// even though the code takes two. Backed by a real <see cref="FleetRepository"/> over throwaway SQLite, because
/// what is being asserted is which rosters a character ends up on.
/// </summary>
public class FleetSwitchTests
{
    private const int Owner = 5100;      // commands Sunday DED run
    private const int Aurel = 5200;      // commands the Sansha evening, which started first
    private const int Tessa = 5300;      // on both rosters; counts for the Sansha evening
    private const int Kaska = 5400;      // on Sunday DED run only
    private const int Vaari = 5500;      // external

    private static readonly DateTimeOffset Now = new(2026, 9, 4, 21, 42, 0, TimeSpan.Zero);

    private readonly SqliteServerDbContextFactory _factory = new();

    /// <summary>Routes the commands the handlers send on to their real handlers, and records the messages, so what a
    /// pilot would actually be sent is assertable without a message store.</summary>
    private sealed class Harness(FleetRepository repository) : IDispatcher
    {
        public List<EnqueueMessageCommand> Messages { get; } = [];

        public async Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) =>
            query switch
            {
                ListMembersActiveElsewhereQuery elsewhere =>
                    (TResult)await new ListMembersActiveElsewhereQueryHandler(repository).Handle(elsewhere, cancellationToken),
                _ => throw new NotSupportedException(query.GetType().Name),
            };

        public Task Send(ICommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
        {
            switch (command)
            {
                case EnqueueMessageCommand message:
                    Messages.Add(message);
                    return (TResult)(object)Result<long>.Success(Messages.Count);
                case SwitchToFleetCommand switching:
                    return (TResult)(object)await new SwitchToFleetCommandHandler(repository, this).Handle(switching, cancellationToken);
                default:
                    throw new NotSupportedException(command.GetType().Name);
            }
        }
    }

    /// <summary>
    /// The scene both screens are drawn from: the Sansha evening started at 21:34 with Tessa on it, Sunday DED run
    /// starts at 21:42 with Tessa on its roster too. Tessa counts for the Sansha evening, because it started first —
    /// the same activated-first tiebreak the broadcast resolver applies.
    /// </summary>
    private async Task<(FleetRepository Repo, Harness Harness, long Sunday, long Sansha)> SceneAsync(
        CancellationToken ct, FleetVisibility sundayVisibility = FleetVisibility.InviteOnly, bool tessaOnSunday = true)
    {
        var repo = new FleetRepository(_factory);

        var sansha = await repo.AddAsync(new FleetEntity
        {
            Name = "Sansha evening Otanuomi",
            CreatorCharacterId = Aurel,
            State = FleetState.Active,
            Activation = FleetActivation.Active,
            ActivatedAt = Now - TimeSpan.FromMinutes(8),
            Visibility = FleetVisibility.Public,
        }, ct);
        await repo.AddMemberAsync(new FleetMember { FleetId = sansha, CharacterId = Aurel, Role = FleetRole.FleetCommander, WingId = -1, SquadId = -1 }, ct);
        await repo.AddMemberAsync(new FleetMember { FleetId = sansha, CharacterId = Tessa, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1 }, ct);

        var sunday = await repo.AddAsync(new FleetEntity
        {
            Name = "Sunday DED run",
            CreatorCharacterId = Owner,
            State = FleetState.Active,
            Activation = FleetActivation.Active,
            ActivatedAt = Now,
            Visibility = sundayVisibility,
        }, ct);
        await repo.AddMemberAsync(new FleetMember { FleetId = sunday, CharacterId = Owner, Role = FleetRole.FleetCommander, WingId = -1, SquadId = -1 }, ct);
        await repo.AddMemberAsync(new FleetMember { FleetId = sunday, CharacterId = Kaska, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1 }, ct);
        await repo.AddMemberAsync(new FleetMember { FleetId = sunday, CharacterId = Vaari, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1, IsExternal = true }, ct);
        if (tessaOnSunday)
            await repo.AddMemberAsync(new FleetMember { FleetId = sunday, CharacterId = Tessa, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1 }, ct);

        return (repo, new Harness(repo), sunday, sansha);
    }

    // ── Reading the collision ───────────────────────────────────────────────────────────────────────

    /// <summary>The one summary line's arithmetic: the member who counts elsewhere, named, and nobody else. An
    /// external is skipped — they have no client and could not be anywhere.</summary>
    [Fact]
    public async Task MembersActiveElsewhere_NamesOnlyTheMemberWhoCountsSomewhereElse()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, _, sunday, sansha) = await SceneAsync(ct);

        var elsewhere = await new ListMembersActiveElsewhereQueryHandler(repo)
            .Handle(new ListMembersActiveElsewhereQuery(sunday), ct);

        var one = Assert.Single(elsewhere);
        Assert.Equal(Tessa, one.CharacterId);
        Assert.Equal(sansha, one.ElsewhereFleetId);
        Assert.Equal("Sansha evening Otanuomi", one.ElsewhereFleetName);
    }

    // ── The commander's one button ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// One press, one request per colliding member, and — the point of the whole ticket — <b>no roster is touched</b>.
    /// Tessa is still on both rosters afterwards and still counts for the fleet she was in.
    /// </summary>
    [Fact]
    public async Task AskingThemAll_SendsOneRequestEach_AndMovesNobody()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, sansha) = await SceneAsync(ct);

        var result = await new RequestFleetSwitchCommandHandler(repo, harness)
            .Handle(new RequestFleetSwitchCommand(sunday, Owner), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);

        var sent = Assert.Single(harness.Messages);
        Assert.Equal(Tessa, sent.RecipientCharacterId);
        Assert.Equal(MessageKind.FleetSwitchRequest, sent.Kind);
        Assert.Equal(sunday, sent.RefId);   // the fleet to come to, so the answer knows where to go

        // Nothing moved: both rosters are as they were, and Tessa still counts for the earlier fleet.
        Assert.True(await repo.IsMemberAsync(sansha, Tessa, ct));
        Assert.True(await repo.IsMemberAsync(sunday, Tessa, ct));
    }

    /// <summary>Only the commander may ask. A member pressing it — or anyone else — is refused, and nothing is sent.</summary>
    [Fact]
    public async Task AskingThemAll_ByAnyoneButTheCommander_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, _) = await SceneAsync(ct);

        var result = await new RequestFleetSwitchCommandHandler(repo, harness)
            .Handle(new RequestFleetSwitchCommand(sunday, Kaska), ct);

        Assert.False(result.IsSuccess);
        Assert.Empty(harness.Messages);
    }

    /// <summary>The member row's ask is the same act at the scale of one pilot: it reaches that pilot and no other.</summary>
    [Fact]
    public async Task AskingOneMember_ReachesOnlyThatMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, _) = await SceneAsync(ct);

        var nobody = await new RequestFleetSwitchCommandHandler(repo, harness)
            .Handle(new RequestFleetSwitchCommand(sunday, Owner, OnlyCharacterId: Kaska), ct);

        Assert.True(nobody.IsSuccess);
        Assert.Equal(0, nobody.Value);   // Kaska is not elsewhere; there is nothing to ask them
        Assert.Empty(harness.Messages);

        var asked = await new RequestFleetSwitchCommandHandler(repo, harness)
            .Handle(new RequestFleetSwitchCommand(sunday, Owner, OnlyCharacterId: Tessa), ct);

        Assert.Equal(1, asked.Value);
        Assert.Equal(Tessa, Assert.Single(harness.Messages).RecipientCharacterId);
    }

    /// <summary>A fleet standing by has nobody linked to it, so a request to come over would ask a pilot to leave a
    /// running fleet for one that is not running.</summary>
    [Fact]
    public async Task AskingThemAll_BeforeTheFleetIsStarted_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, _) = await SceneAsync(ct);
        var fleet = await repo.GetAsync(sunday, ct);
        fleet!.Activation = FleetActivation.Forming;
        await repo.UpdateAsync(fleet, ct);

        var result = await new RequestFleetSwitchCommandHandler(repo, harness)
            .Handle(new RequestFleetSwitchCommand(sunday, Owner), ct);

        Assert.False(result.IsSuccess);
        Assert.Empty(harness.Messages);
    }

    // ── The member's own switch ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The two acts as one: Tessa comes off the Sansha roster and counts for Sunday DED run — and the Sansha fleet
    /// keeps running for everyone else on it.
    /// </summary>
    [Fact]
    public async Task Switching_LeavesTheOtherFleet_AndCountsHere()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, sansha) = await SceneAsync(ct);

        var result = await new SwitchToFleetCommandHandler(repo, harness)
            .Handle(new SwitchToFleetCommand(sunday, Tessa), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(sansha, Assert.Single(result.Value!));

        Assert.False(await repo.IsMemberAsync(sansha, Tessa, ct));
        Assert.True(await repo.IsMemberAsync(sunday, Tessa, ct));

        // The one fleet Tessa now counts for is this one.
        var active = await repo.ListActiveMembershipsAsync(Tessa, ct);
        Assert.Equal(sunday, Assert.Single(active).FleetId);

        // The fleet she left is still running, with its commander on it.
        var left = await repo.GetAsync(sansha, ct);
        Assert.Equal(FleetActivation.Active, left!.Activation);
        Assert.True(await repo.IsMemberAsync(sansha, Aurel, ct));
    }

    /// <summary>
    /// The step this ticket exists for: hooking up later to an <b>invite-only</b> fleet. JoinFleet refuses one on
    /// visibility alone, so a member who does nothing tonight would have had no way back in. Being on the roster is
    /// enough — the only thing between them and counting here was the fleet they were still in.
    /// </summary>
    [Fact]
    public async Task Switching_IntoAnInviteOnlyFleetTheyAreAlreadyRosteredIn_Works()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, _) = await SceneAsync(ct, sundayVisibility: FleetVisibility.InviteOnly);

        // The door JOIN closes, measured rather than assumed: JoinFleetCommandHandler tests visibility before it
        // tests membership, so it refuses even a pilot who is already on the roster. Hooking up later through JOIN
        // is therefore shut on an invite-only fleet — which is precisely why this route exists.
        var join = await new JoinFleetCommandHandler(repo).Handle(new JoinFleetCommand(sunday, Tessa), ct);
        Assert.False(join.IsSuccess);

        var result = await new SwitchToFleetCommandHandler(repo, harness)
            .Handle(new SwitchToFleetCommand(sunday, Tessa), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(sunday, Assert.Single(await repo.ListActiveMembershipsAsync(Tessa, ct)).FleetId);
    }

    /// <summary>
    /// The other half of the same door: an outstanding invite to an invite-only fleet. Accepting it while still
    /// flying elsewhere is refused outright, and the invite deliberately stays Pending for it. Switching accepts it
    /// and leaves the other fleet in one act — which is exactly the refusal turned into a route.
    /// </summary>
    [Fact]
    public async Task Switching_AcceptsAnOutstandingInvite_ThatCouldNotBeAcceptedWhileFlyingElsewhere()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, sansha) = await SceneAsync(ct, tessaOnSunday: false);

        var inviteId = await repo.AddInviteAsync(new FleetInvite
        {
            FleetId = sunday,
            InviterCharacterId = Owner,
            InviteeCharacterId = Tessa,
            Role = FleetRole.SquadMember,
            Status = FleetInviteStatus.Pending,
            CreatedAt = Now,
        }, ct);

        // What happens without the switch: refused, and the invite is left standing on purpose.
        var refused = await new RespondToFleetInviteCommandHandler(repo)
            .Handle(new RespondToFleetInviteCommand(inviteId, Accept: true, ActingCharacterId: Tessa), ct);
        Assert.False(refused.IsSuccess);
        Assert.Equal(FleetInviteStatus.Pending, (await repo.GetInviteAsync(inviteId, ct))!.Status);

        var switched = await new SwitchToFleetCommandHandler(repo, harness)
            .Handle(new SwitchToFleetCommand(sunday, Tessa), ct);

        Assert.True(switched.IsSuccess);
        Assert.Equal(FleetInviteStatus.Accepted, (await repo.GetInviteAsync(inviteId, ct))!.Status);
        Assert.True(await repo.IsMemberAsync(sunday, Tessa, ct));
        Assert.False(await repo.IsMemberAsync(sansha, Tessa, ct));
    }

    /// <summary>A pilot who is not on the roster and holds no invite is not smuggled into an invite-only fleet — and
    /// crucially the refusal comes <i>before</i> anything is left, so they are not stranded out of both.</summary>
    [Fact]
    public async Task Switching_IntoAnInviteOnlyFleetWithNoSeat_IsRefusedBeforeAnythingIsLeft()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, sansha) = await SceneAsync(ct, tessaOnSunday: false);

        var result = await new SwitchToFleetCommandHandler(repo, harness)
            .Handle(new SwitchToFleetCommand(sunday, Tessa), ct);

        Assert.False(result.IsSuccess);
        Assert.True(await repo.IsMemberAsync(sansha, Tessa, ct));   // still where she was
    }

    /// <summary>A commander cannot walk out of their own fleet — it would leave it ownerless — and the refusal comes
    /// before any roster is touched.</summary>
    [Fact]
    public async Task Switching_OutOfAFleetYouCommand_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, sansha) = await SceneAsync(ct);
        await repo.AddMemberAsync(new FleetMember { FleetId = sunday, CharacterId = Aurel, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1 }, ct);

        var result = await new SwitchToFleetCommandHandler(repo, harness)
            .Handle(new SwitchToFleetCommand(sunday, Aurel), ct);

        Assert.False(result.IsSuccess);
        Assert.True(await repo.IsMemberAsync(sansha, Aurel, ct));
    }

    /// <summary>A request stands only while the fleet runs: switching into one that has stopped would leave the
    /// pilot counting for nothing at all.</summary>
    [Fact]
    public async Task Switching_IntoAFleetThatHasStopped_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, sansha) = await SceneAsync(ct);
        var fleet = await repo.GetAsync(sunday, ct);
        fleet!.Activation = FleetActivation.Forming;
        await repo.UpdateAsync(fleet, ct);

        var result = await new SwitchToFleetCommandHandler(repo, harness)
            .Handle(new SwitchToFleetCommand(sunday, Tessa), ct);

        Assert.False(result.IsSuccess);
        Assert.True(await repo.IsMemberAsync(sansha, Tessa, ct));
    }

    // ── Answering the request ───────────────────────────────────────────────────────────────────────

    /// <summary>"No, I'll stay where I am" is not leaving. The pilot keeps both seats, still counts where they were,
    /// and next week the fleet is there with them on it — the distinction the whole design turns on.</summary>
    [Fact]
    public async Task DecliningTheRequest_ChangesNothingAtAll()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, sansha) = await SceneAsync(ct);
        var message = new QueuedMessage { Id = 1, RecipientCharacterId = Tessa, Kind = MessageKind.FleetSwitchRequest, RefId = sunday };

        var result = await new FleetSwitchRequestResponder(harness).RespondAsync(message, accept: false, Tessa, ct);

        Assert.True(result.IsSuccess);
        Assert.True(await repo.IsMemberAsync(sunday, Tessa, ct));
        Assert.True(await repo.IsMemberAsync(sansha, Tessa, ct));

        // Still on both rosters, and still counting for the one that started first — declining is not leaving.
        var active = await repo.ListActiveMembershipsAsync(Tessa, ct);
        Assert.Equal(2, active.Count);
        Assert.Equal(sansha, active.OrderBy(m => m.ActivatedAt ?? DateTimeOffset.MaxValue).First().FleetId);
    }

    /// <summary>Saying yes runs the switch, so the request and the act are the same button.</summary>
    [Fact]
    public async Task AcceptingTheRequest_Switches()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, sunday, sansha) = await SceneAsync(ct);
        var message = new QueuedMessage { Id = 1, RecipientCharacterId = Tessa, Kind = MessageKind.FleetSwitchRequest, RefId = sunday };

        var result = await new FleetSwitchRequestResponder(harness).RespondAsync(message, accept: true, Tessa, ct);

        Assert.True(result.IsSuccess);
        Assert.False(await repo.IsMemberAsync(sansha, Tessa, ct));
        Assert.Equal(sunday, Assert.Single(await repo.ListActiveMembershipsAsync(Tessa, ct)).FleetId);
    }
}
