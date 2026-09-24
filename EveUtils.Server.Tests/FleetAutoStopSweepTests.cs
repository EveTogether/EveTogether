using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Grpc;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Cleanup;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using EveUtils.Shared.Modules.Messaging.Commands;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>
/// The server standing a fleet down by itself (ET-167), through the real <see cref="FleetAutoStopRunner"/>, the real
/// <see cref="StopFleetCommandHandler"/> and a real <see cref="FleetRepository"/> over throwaway SQLite — because the
/// thing worth proving is not that the policy returns a value but that the resulting command gets past the
/// creator-only guard on its own terms, and leaves the roster where it was.
///
/// This is the first path on which the server changes a fleet's phase without anyone having authenticated, so the
/// attribution is asserted as hard as the outcome: a sweep that could not act as the owner would simply be refused
/// by <c>FleetStructureGuard</c>, and a fleet that came back Forming is the proof that it was not.
/// </summary>
public sealed class FleetAutoStopSweepTests : IAsyncLifetime
{
    private const int Owner = 4001;
    private const int Flying = 4002;
    private const int Departed = 4003;
    private const int Onlooker = 4004;

    private static readonly FleetCleanupOptions Options = FleetCleanupOptions.Default;
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 20, 30, 0, TimeSpan.Zero);

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly ConnectedClients _clients = new();
    private readonly InProcessEventBus _bus = new();
    private readonly ServiceProvider _services;
    private readonly FleetChangeAnnouncer _announcer;

    public FleetAutoStopSweepTests()
    {
        _services = new ServiceCollection().AddSingleton<IFleetReader>(new FleetRepository(_factory)).BuildServiceProvider();
        _announcer = new FleetChangeAnnouncer(
            _bus, _services.GetRequiredService<IServiceScopeFactory>(), _clients, NullLogger<FleetChangeAnnouncer>.Instance);
    }

    public ValueTask InitializeAsync() => new(_announcer.StartAsync(CancellationToken.None));

    public async ValueTask DisposeAsync()
    {
        _announcer.Dispose();
        await _services.DisposeAsync();
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Routes StopFleetCommand to the real handler and records every message the handler enqueues, so the
    /// mail a pilot would actually receive is assertable without a message store.</summary>
    private sealed class Harness(FleetRepository repository, IEventBus bus) : IDispatcher
    {
        public List<EnqueueMessageCommand> Messages { get; } = [];
        public List<StopFleetCommand> Stops { get; } = [];

        public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Send(ICommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
        {
            switch (command)
            {
                case StopFleetCommand stop:
                    Stops.Add(stop);
                    var handler = new StopFleetCommandHandler(repository, this, bus);
                    return (TResult)(object)await handler.Handle(stop, cancellationToken);
                case EnqueueMessageCommand message:
                    Messages.Add(message);
                    return (TResult)(object)Result<long>.Success(Messages.Count);
                default:
                    throw new NotSupportedException(command.GetType().Name);
            }
        }
    }

    private async Task<(FleetRepository Repo, Harness Harness, FleetAutoStopRunner Runner, long FleetId)> StartedFleetAsync(
        CancellationToken ct, DateTimeOffset? lastActivityAt = null, FleetVisibility visibility = FleetVisibility.InviteOnly,
        params int[] members)
    {
        var repo = new FleetRepository(_factory);
        var fleetId = await repo.AddAsync(new FleetEntity
        {
            Name = "Wednesday Homefronts",
            CreatorCharacterId = Owner,
            Visibility = visibility,
            State = FleetState.Active,
            Activation = FleetActivation.Active,
            ActivatedAt = Now - TimeSpan.FromHours(3),
            LastActivityAt = lastActivityAt ?? Now - TimeSpan.FromHours(2),
        }, ct);

        foreach (var characterId in members)
            await repo.AddMemberAsync(new FleetMember
            {
                FleetId = fleetId, CharacterId = characterId, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1,
            }, ct);

        var harness = new Harness(repo, _bus);
        return (repo, harness, new FleetAutoStopRunner(repo, harness, NullLogger<FleetAutoStopRunner>.Instance), fleetId);
    }

    private static DateTimeOffset Silent => Now - FleetMemberPresence.SilentAfter - TimeSpan.FromMinutes(5);
    private static DateTimeOffset JustHeard => Now - TimeSpan.FromSeconds(10);

    // ── Everyone offline ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole ticket in one test: every member's client has gone quiet, so the server stands the fleet down
    /// itself — as the owner, with the roster untouched and the reason recorded.
    /// </summary>
    [Fact]
    public async Task EveryMemberGoneQuiet_TheServerStandsTheFleetDown_AsItsOwner_WithTheRosterIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, runner, fleetId) = await StartedFleetAsync(ct, members: [Flying, Departed]);
        await repo.TouchMemberSeenAsync(fleetId, Flying, Silent, ct);
        await repo.TouchMemberSeenAsync(fleetId, Departed, Silent, ct);

        var swept = await runner.SweepAsync(Now, Options, brakeEngaged: false, ct);

        Assert.Equal(1, swept.AllOffline);
        Assert.Equal(FleetActivation.Forming, (await repo.GetAsync(fleetId, ct))!.Activation);
        Assert.Equal(2, (await repo.ListMembersAsync(fleetId, ct)).Count);

        var stop = Assert.Single(harness.Stops);
        Assert.Equal(Owner, stop.ActingCharacterId);
        Assert.Equal(FleetStopTrigger.AllMembersOffline, stop.Trigger);
    }

    /// <summary>The acceptance criterion: one pilot still there and nothing happens, however quiet the rest are.</summary>
    [Fact]
    public async Task OneMemberStillFlying_KeepsTheFleetRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, runner, fleetId) = await StartedFleetAsync(ct, members: [Flying, Departed]);
        await repo.TouchMemberSeenAsync(fleetId, Flying, JustHeard, ct);
        await repo.TouchMemberSeenAsync(fleetId, Departed, Silent, ct);

        await runner.SweepAsync(Now, Options, brakeEngaged: false, ct);

        Assert.Equal(FleetActivation.Active, (await repo.GetAsync(fleetId, ct))!.Activation);
        Assert.Empty(harness.Stops);
    }

    /// <summary>
    /// The failure mode the brake exists for. Same fleet, same silence, sweeping at 11:01 UTC — the moment
    /// Tranquility took everyone's clients down with it — and nothing happens.
    /// </summary>
    [Fact]
    public async Task DuringTheDailyWindow_TheSameSilenceStopsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var downtime = new DateTimeOffset(2026, 9, 4, 11, 1, 0, TimeSpan.Zero);
        var (repo, harness, runner, fleetId) = await StartedFleetAsync(
            ct, lastActivityAt: downtime - TimeSpan.FromHours(2), members: [Flying, Departed]);
        await repo.TouchMemberSeenAsync(fleetId, Flying, downtime - TimeSpan.FromMinutes(10), ct);
        await repo.TouchMemberSeenAsync(fleetId, Departed, downtime - TimeSpan.FromMinutes(10), ct);

        var brake = FleetAutoStopBrake.IsEngaged(downtime, esiUsable: true, null, Options.ReconnectGrace);
        Assert.True(brake);

        await runner.SweepAsync(downtime, Options, brake, ct);

        Assert.Equal(FleetActivation.Active, (await repo.GetAsync(fleetId, ct))!.Activation);
        Assert.Empty(harness.Stops);
    }

    // ── Everyone left ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEmptiedRoster_StandsTheFleetDown_EvenWithTheBrakeOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, runner, fleetId) = await StartedFleetAsync(ct);

        var swept = await runner.SweepAsync(Now, Options, brakeEngaged: true, ct);

        Assert.Equal(1, swept.RosterEmpty);
        Assert.Equal(FleetActivation.Forming, (await repo.GetAsync(fleetId, ct))!.Activation);
        Assert.Equal(FleetStopTrigger.RosterEmpty, Assert.Single(harness.Stops).Trigger);
    }

    /// <summary>Start a fleet, then invite: the roster is legitimately empty in between and the sweep must wait.</summary>
    [Fact]
    public async Task AFleetThatWasJustStarted_IsNotStoodDownWhileItsInvitationsAreOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, runner, fleetId) = await StartedFleetAsync(ct, lastActivityAt: Now - TimeSpan.FromMinutes(1));

        await runner.SweepAsync(Now, Options, brakeEngaged: false, ct);

        Assert.Equal(FleetActivation.Active, (await repo.GetAsync(fleetId, ct))!.Activation);
        Assert.Empty(harness.Stops);
    }

    /// <summary>
    /// A fleet started ahead of its pilots. Nobody has published into it yet, so there is no silence to read — and
    /// without this it would stand itself down ninety seconds after the FC pressed START.
    /// </summary>
    [Fact]
    public async Task AFleetWhoseMembersHaveNotArrivedYet_KeepsRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, runner, fleetId) = await StartedFleetAsync(ct, members: [Flying, Departed]);

        await runner.SweepAsync(Now, Options, brakeEngaged: false, ct);

        Assert.Equal(FleetActivation.Active, (await repo.GetAsync(fleetId, ct))!.Activation);
        Assert.Empty(harness.Stops);
    }

    // ── What the pilots are told ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// "Onderscheid dat ook in het bericht aan de leden" — an automatic stop may not read like the FC pressing a
    /// button. And the owner is written to as well, which a manual stop deliberately does not do: here they pressed
    /// nothing and may not even have had a client running.
    /// </summary>
    [Fact]
    public async Task AnAutomaticStop_ReadsDifferentlyFromAManualOne_AndReachesTheOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, runner, fleetId) = await StartedFleetAsync(ct, members: [Flying]);
        await repo.TouchMemberSeenAsync(fleetId, Flying, Silent, ct);

        await runner.SweepAsync(Now, Options, brakeEngaged: false, ct);

        var toMember = Assert.Single(harness.Messages, m => m.RecipientCharacterId == Flying);
        Assert.Contains("automatically", toMember.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("on its own", toMember.Body!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("offline", toMember.Body!, StringComparison.OrdinalIgnoreCase);

        Assert.Single(harness.Messages, m => m.RecipientCharacterId == Owner);
    }

    /// <summary>The manual stop is untouched by all of this: same words, and the owner is still not mailed about
    /// the button they just pressed.</summary>
    [Fact]
    public async Task AManualStop_StillReadsAsOneAndStillDoesNotMailTheOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, _, fleetId) = await StartedFleetAsync(ct, members: [Flying]);

        var result = await harness.Send(new StopFleetCommand(fleetId, Owner), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(FleetActivation.Forming, (await repo.GetAsync(fleetId, ct))!.Activation);
        var toMember = Assert.Single(harness.Messages);
        Assert.Equal(Flying, toMember.RecipientCharacterId);
        Assert.DoesNotContain("automatically", toMember.Title, StringComparison.OrdinalIgnoreCase);
    }

    // ── Who hears it (ET-10) ────────────────────────────────────────────────────────────────────────

    /// <summary>A pilot with the fleet list open who is on no roster: a public fleet standing itself down is news on
    /// their screen, and before ET-10 only its members and owner were told.</summary>
    [Fact]
    public async Task APublicFleetStandingDown_ReachesAConnectedNonMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var onlooker = Connect(Onlooker);
        var (_, _, runner, fleetId) = await StartedFleetAsync(ct, visibility: FleetVisibility.Public);

        await runner.SweepAsync(Now, Options, brakeEngaged: false, ct);

        var received = Assert.Single(onlooker.Written).Event;
        Assert.Equal("fleet.changed", received.EventType);
        Assert.Equal(fleetId, received.FleetId);
    }

    /// <summary>
    /// The boundary: in one sweep an invite-only fleet and a public one both stand down. The onlooker hears the public
    /// one only, and what they hear names the fleet and what happened to it — never who is on its roster.
    /// </summary>
    [Fact]
    public async Task AnInviteOnlyFleetStandingDown_StaysOnItsRoster_AndThePublicPushCarriesNoRoster()
    {
        var ct = TestContext.Current.CancellationToken;
        var onlooker = Connect(Onlooker);
        var member = Connect(Flying);
        var (repo, _, runner, hiddenId) = await StartedFleetAsync(ct, members: [Flying]);
        await repo.TouchMemberSeenAsync(hiddenId, Flying, Silent, ct);
        var (_, _, _, publicId) = await StartedFleetAsync(ct, visibility: FleetVisibility.Public, members: [Departed]);
        await repo.TouchMemberSeenAsync(publicId, Departed, Silent, ct);

        await runner.SweepAsync(Now, Options, brakeEngaged: false, ct);

        Assert.Contains(member.Written, envelope => envelope.Event.FleetId == hiddenId);
        var received = Assert.Single(onlooker.Written).Event;
        Assert.Equal(publicId, received.FleetId);
        Assert.DoesNotContain(Departed.ToString(), received.PayloadJson);
        Assert.DoesNotContain(Owner.ToString(), received.PayloadJson);
        Assert.Equal(0, received.CharacterId);
        Assert.Equal(0, received.TargetCharacterId);
    }

    private RecordingWriter Connect(int characterId)
    {
        var writer = new RecordingWriter();
        _clients.Add(new ConnectedClient($"conn-{characterId}", characterId, $"Pilot {characterId}", writer));
        return writer;
    }

    private sealed class RecordingWriter : IServerStreamWriter<ServerEnvelope>
    {
        public List<ServerEnvelope> Written { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ServerEnvelope message) => WriteAsync(message, CancellationToken.None);
        public Task WriteAsync(ServerEnvelope message, CancellationToken cancellationToken)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }

    // ── Phases the sweep may not touch ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FleetActivation.Forming)]
    [InlineData(FleetActivation.Concluded)]
    public async Task AFleetThatIsNotRunning_IsLeftAlone(FleetActivation activation)
    {
        var ct = TestContext.Current.CancellationToken;
        var (repo, harness, runner, fleetId) = await StartedFleetAsync(ct);
        var fleet = (await repo.GetAsync(fleetId, ct))!;
        fleet.Activation = activation;
        await repo.UpdateAsync(fleet, ct);

        await runner.SweepAsync(Now, Options, brakeEngaged: false, ct);

        Assert.Equal(activation, (await repo.GetAsync(fleetId, ct))!.Activation);
        Assert.Empty(harness.Stops);
    }
}
