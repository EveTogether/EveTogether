using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Queries;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using EveUtils.Shared.Modules.Messaging.Commands;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>
/// Signing off a Forming fleet's next start without leaving the roster (ET-169). Two things carry the whole
/// feature: the command is <b>self-only, strictly</b> — unlike every other roster command in this module, not
/// even the fleet's creator has standing to set it for someone else — and a sign-off is <b>consumed by the next
/// start</b>, never by a stop/restart of the fleet itself. Backed by a real <see cref="FleetRepository"/> over
/// throwaway SQLite, because what is being asserted is what ends up stored on the roster row.
/// </summary>
public class FleetMemberAvailabilityTests
{
    private const int Owner = 6100;
    private const int Kaska = 6200;   // an ordinary member, signs off
    private const int Tessa = 6300;   // an ordinary member, says nothing
    private const int Vaari = 6400;   // external — no client, cannot sign off

    private readonly SqliteServerDbContextFactory _factory = new();

    /// <summary>Routes <see cref="EnqueueMessageCommand"/> to a recording list and nothing else — the only
    /// command <see cref="StartFleetCommandHandler"/> sends downstream.</summary>
    private sealed class Harness : IDispatcher
    {
        public List<EnqueueMessageCommand> Messages { get; } = [];

        public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(query.GetType().Name);

        public Task Send(ICommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
        {
            if (command is EnqueueMessageCommand message)
            {
                Messages.Add(message);
                return Task.FromResult((TResult)(object)Result<long>.Success(Messages.Count));
            }
            throw new NotSupportedException(command.GetType().Name);
        }
    }

    private async Task<(FleetRepository Repo, long FleetId, long KaskaMemberId, long TessaMemberId)> SceneAsync(
        CancellationToken ct, FleetActivation activation = FleetActivation.Forming)
    {
        var repo = new FleetRepository(_factory);
        var fleetId = await repo.AddAsync(new FleetEntity
        {
            Name = "Sunday DED run", CreatorCharacterId = Owner, State = FleetState.Active, Activation = activation,
        }, ct);

        await repo.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = Owner, Role = FleetRole.FleetCommander, WingId = -1, SquadId = -1 }, ct);
        var kaskaId = await repo.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = Kaska, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1 }, ct);
        var tessaId = await repo.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = Tessa, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1 }, ct);
        await repo.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = Vaari, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1, IsExternal = true }, ct);

        return (repo, fleetId, kaskaId, tessaId);
    }

    // ── The self-only guard ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SigningOff_ByTheMemberThemselves_Succeeds_AndStaysOnTheRoster()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, _, kaskaId, _) = await SceneAsync(ct);

        var result = await new SetFleetMemberAvailabilityCommandHandler(repo).Handle(
            new SetFleetMemberAvailabilityCommand(kaskaId, FleetMemberAvailability.SignedOff, "can't make Sunday", Kaska), ct);

        Assert.True(result.IsSuccess);
        var member = await repo.GetMemberAsync(kaskaId, ct);
        Assert.Equal(FleetMemberAvailability.SignedOff, member!.Availability);
        Assert.Equal("can't make Sunday", member.AvailabilityNote);
        Assert.NotNull(member.AvailabilityUpdatedAt);
        Assert.True(await repo.IsMemberAsync((await repo.GetAsync(member.FleetId, ct))!.Id, Kaska, ct));
    }

    /// <summary>The one guard this command inverts: every other roster command in this module lets the creator
    /// act for a member. This one does not, on purpose — availability is the member's own signal.</summary>
    [Fact]
    public async Task SigningOff_ForSomeoneElse_ByTheFleetsOwnCreator_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, _, kaskaId, _) = await SceneAsync(ct);

        var result = await new SetFleetMemberAvailabilityCommandHandler(repo).Handle(
            new SetFleetMemberAvailabilityCommand(kaskaId, FleetMemberAvailability.SignedOff, null, Owner), ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FleetMemberAvailability.NotSet, (await repo.GetMemberAsync(kaskaId, ct))!.Availability);
    }

    [Fact]
    public async Task SigningOff_ForSomeoneElse_ByAnotherMember_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, _, kaskaId, _) = await SceneAsync(ct);

        var result = await new SetFleetMemberAvailabilityCommandHandler(repo).Handle(
            new SetFleetMemberAvailabilityCommand(kaskaId, FleetMemberAvailability.SignedOff, null, Tessa), ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FleetMemberAvailability.NotSet, (await repo.GetMemberAsync(kaskaId, ct))!.Availability);
    }

    /// <summary>An external has no client — nobody could ever satisfy the self-only check for them — but the
    /// refusal has its own message rather than falling through to a permission error that reads like a bug.</summary>
    [Fact]
    public async Task SigningOff_AnExternalMember_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId, _, _) = await SceneAsync(ct);
        var vaari = Assert.Single(await repo.ListMembersAsync(fleetId, ct), m => m.CharacterId == Vaari);

        var result = await new SetFleetMemberAvailabilityCommandHandler(repo).Handle(
            new SetFleetMemberAvailabilityCommand(vaari.Id, FleetMemberAvailability.SignedOff, null, Vaari), ct);

        Assert.False(result.IsSuccess);
    }

    /// <summary>Signing off answers "will you be there next time this starts" — meaningless once the fleet has
    /// already started.</summary>
    [Fact]
    public async Task SigningOff_OnAFleetThatHasAlreadyStarted_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, _, kaskaId, _) = await SceneAsync(ct, FleetActivation.Active);

        var result = await new SetFleetMemberAvailabilityCommandHandler(repo).Handle(
            new SetFleetMemberAvailabilityCommand(kaskaId, FleetMemberAvailability.SignedOff, null, Kaska), ct);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ReversingASignOff_ToAvailable_Works()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, _, kaskaId, _) = await SceneAsync(ct);
        var handler = new SetFleetMemberAvailabilityCommandHandler(repo);
        await handler.Handle(new SetFleetMemberAvailabilityCommand(kaskaId, FleetMemberAvailability.SignedOff, "busy", Kaska), ct);

        var result = await handler.Handle(new SetFleetMemberAvailabilityCommand(kaskaId, FleetMemberAvailability.Available, null, Kaska), ct);

        Assert.True(result.IsSuccess);
        var member = await repo.GetMemberAsync(kaskaId, ct);
        Assert.Equal(FleetMemberAvailability.Available, member!.Availability);
        Assert.Null(member.AvailabilityNote);
    }

    // ── What starting does with a sign-off ──────────────────────────────────────────────────────────

    /// <summary>The acceptance criterion, word for word: a signed-off member is neither notified nor counted as
    /// a collision — those are two different things and the tally must keep them apart.</summary>
    [Fact]
    public async Task StartingTheFleet_SkipsASignedOffMember_AndDoesNotCountThemAsACollision()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId, kaskaId, _) = await SceneAsync(ct);
        var harness = new Harness();
        await new SetFleetMemberAvailabilityCommandHandler(repo).Handle(
            new SetFleetMemberAvailabilityCommand(kaskaId, FleetMemberAvailability.SignedOff, "can't make it", Kaska), ct);

        var result = await new StartFleetCommandHandler(repo, harness).Handle(new StartFleetCommand(fleetId, Owner), ct);

        Assert.True(result.IsSuccess);
        // Only Tessa is notified: the creator is skipped because they pressed start, the external has no inbox,
        // and Kaska is skipped because they signed off — none of those three is a collision message either.
        var recipient = Assert.Single(harness.Messages).RecipientCharacterId;
        Assert.Equal(Tessa, recipient);
        Assert.DoesNotContain(harness.Messages, m => m.RecipientCharacterId == Kaska);
        // No "N member(s) already elsewhere" summary to the creator — a sign-off is not that kind of gap.
        Assert.DoesNotContain(harness.Messages, m => m.RecipientCharacterId == Owner);
    }

    /// <summary>A sign-off covers only the next start — not every future one — so it is consumed the moment
    /// that start happens, whether or not the member ended up skipped.</summary>
    [Fact]
    public async Task StartingTheFleet_ResetsEverySignedOffOrConfirmedMember_BackToNotSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId, kaskaId, tessaId) = await SceneAsync(ct);
        var harness = new Harness();
        var handler = new SetFleetMemberAvailabilityCommandHandler(repo);
        await handler.Handle(new SetFleetMemberAvailabilityCommand(kaskaId, FleetMemberAvailability.SignedOff, "can't make it", Kaska), ct);
        await handler.Handle(new SetFleetMemberAvailabilityCommand(tessaId, FleetMemberAvailability.Available, null, Tessa), ct);

        await new StartFleetCommandHandler(repo, harness).Handle(new StartFleetCommand(fleetId, Owner), ct);

        var kaska = await repo.GetMemberAsync(kaskaId, ct);
        Assert.Equal(FleetMemberAvailability.NotSet, kaska!.Availability);
        Assert.Null(kaska.AvailabilityNote);
        Assert.Null(kaska.AvailabilityUpdatedAt);

        var tessa = await repo.GetMemberAsync(tessaId, ct);
        Assert.Equal(FleetMemberAvailability.NotSet, tessa!.Availability);
    }

    /// <summary>The member stays on the roster after the fleet has started too — signing off never removes
    /// anyone, and neither does the reset that follows a start.</summary>
    [Fact]
    public async Task AfterStarting_ASignedOffMember_IsStillOnTheRoster()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, fleetId, kaskaId, _) = await SceneAsync(ct);
        var harness = new Harness();
        await new SetFleetMemberAvailabilityCommandHandler(repo).Handle(
            new SetFleetMemberAvailabilityCommand(kaskaId, FleetMemberAvailability.SignedOff, null, Kaska), ct);

        await new StartFleetCommandHandler(repo, harness).Handle(new StartFleetCommand(fleetId, Owner), ct);

        Assert.True(await repo.IsMemberAsync(fleetId, Kaska, ct));
    }
}
