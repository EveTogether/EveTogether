using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Client.UiTests;

/// <summary>
/// of the ESI fleet-coupling: the boss-side roster mirror. A linked fleet whose boss is our character has
/// its live ESI roster diffed against the plan, and a change broadcasts <see cref="FleetChangeKind.RosterChanged"/> so
/// open windows refresh. Driven over a real repository + event bus (<see cref="TestClientInstance"/>) + a fake fleet client.
/// </summary>
public class EsiFleetSyncServiceTests
{
    [Fact]
    public async Task SyncFleet_MirrorsLiveRoster_DiffsPlan_AndBroadcastsRosterChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await repository.AddAsync(new FleetEntity { Name = "Doctrine", CreatorCharacterId = owner, State = FleetState.Active }, ct);
        foreach (var id in new[] { 100, 200, 300 }) // planned doctrine roster
            await repository.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = id, WingId = -1, SquadId = -1 }, ct);

        var fleet = (await repository.GetAsync(fleetId, ct))!;
        fleet.EsiFleetId = 999;
        fleet.EsiFleetBossId = owner;
        fleet.EsiSyncState = EsiFleetSyncState.Linked;
        await repository.UpdateAsync(fleet, ct);

        // Live in-game: 100 + 200 joined, 300 still missing, 999 is in-game but not in our plan.
        var live = new FakeFleetClient
        {
            Members = [new EsiFleetMember { CharacterId = 100 }, new EsiFleetMember { CharacterId = 200 }, new EsiFleetMember { CharacterId = 999 }],
        };

        FleetChangedEvent? captured = null;
        using var sub = bus.Subscribe<FleetChangedEvent>((evt, _) => { captured = evt; return Task.CompletedTask; });

        var service = new EsiFleetSyncService(live, repository, registry, new NullSessionStore(), new RecordingFleetTransportClient(), bus, new FleetRosterChangeNotifier(new RecordingToastService(), new FakeExternalLookup()), new EsiAvailabilityState(), NullLogger<EsiFleetSyncService>.Instance);
        var diff = await service.SyncFleetAsync(fleet, ct);

        Assert.NotNull(diff);
        Assert.Equal(new[] { 100, 200 }, diff!.Present);
        Assert.Equal(new[] { 300 }, diff.Missing);
        Assert.Equal(new[] { 999 }, diff.External);

        Assert.NotNull(captured);
        Assert.Equal(FleetChangeKind.RosterChanged, captured!.Data.Kind);
        Assert.Equal(fleetId, captured.FleetId);
    }

    [Fact]
    public async Task SyncFleet_NotLinked_DoesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        var fleetId = await repository.AddAsync(new FleetEntity { Name = "Unlinked", CreatorCharacterId = 100, State = FleetState.Active }, ct);
        var fleet = (await repository.GetAsync(fleetId, ct))!; // EsiSyncState stays NotLinked

        var service = new EsiFleetSyncService(new FakeFleetClient(), repository, registry, new NullSessionStore(), new RecordingFleetTransportClient(), bus, new FleetRosterChangeNotifier(new RecordingToastService(), new FakeExternalLookup()), new EsiAvailabilityState(), NullLogger<EsiFleetSyncService>.Instance);
        var diff = await service.SyncFleetAsync(fleet, ct);

        Assert.Null(diff);
    }

    [Fact]
    public async Task SyncFleet_WhenInGameFleetGone_UnlinksAndClearsStaleEsiIds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await repository.AddAsync(new FleetEntity { Name = "Doctrine", CreatorCharacterId = owner, State = FleetState.Active }, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS", EsiWingId = 7001 }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha", EsiSquadId = 8001 }, ct);
        var fleet = (await repository.GetAsync(fleetId, ct))!;
        fleet.EsiFleetId = 999;
        fleet.EsiFleetBossId = owner;
        fleet.EsiSyncState = EsiFleetSyncState.Linked;
        await repository.UpdateAsync(fleet, ct);

        var fake = new FakeFleetClient { MembersError = EsiError.Of(EsiErrorKind.NotFound, "fleet not found", 404) };
        var service = new EsiFleetSyncService(fake, repository, registry, new NullSessionStore(), new RecordingFleetTransportClient(), bus,
            new FleetRosterChangeNotifier(new RecordingToastService(), new FakeExternalLookup()), new EsiAvailabilityState(), NullLogger<EsiFleetSyncService>.Instance);

        // A single NotFound doesn't drop the link (could be a blip); a second consecutive miss confirms it's gone.
        await service.SyncFleetAsync(fleet, ct);
        Assert.Equal(EsiFleetSyncState.Linked, (await repository.GetAsync(fleetId, ct))!.EsiSyncState); // still linked after 1

        var diff = await service.SyncFleetAsync(fleet, ct); // 2nd consecutive miss → unlink

        Assert.Null(diff);
        var reloaded = (await repository.GetAsync(fleetId, ct))!;
        Assert.Equal(EsiFleetSyncState.NotLinked, reloaded.EsiSyncState);
        Assert.Null(reloaded.EsiFleetId);
        Assert.Null((await repository.GetWingAsync(wingId, ct))!.EsiWingId); // stale in-game ids cleared for a clean re-couple
        Assert.Null((await repository.GetSquadAsync(squadId, ct))!.EsiSquadId);
    }

    [Fact]
    public async Task SyncFleet_OnTransientError_KeepsTheLink()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await repository.AddAsync(new FleetEntity { Name = "Doctrine", CreatorCharacterId = owner, State = FleetState.Active }, ct);
        var fleet = (await repository.GetAsync(fleetId, ct))!;
        fleet.EsiFleetId = 999;
        fleet.EsiFleetBossId = owner;
        fleet.EsiSyncState = EsiFleetSyncState.Linked;
        await repository.UpdateAsync(fleet, ct);

        var fake = new FakeFleetClient { MembersError = EsiError.Of(EsiErrorKind.Timeout, "timeout", 504) };
        var service = new EsiFleetSyncService(fake, repository, registry, new NullSessionStore(), new RecordingFleetTransportClient(), bus,
            new FleetRosterChangeNotifier(new RecordingToastService(), new FakeExternalLookup()), new EsiAvailabilityState(), NullLogger<EsiFleetSyncService>.Instance);

        var diff = await service.SyncFleetAsync(fleet, ct);

        Assert.Null(diff);
        var reloaded = (await repository.GetAsync(fleetId, ct))!;
        Assert.Equal(EsiFleetSyncState.Linked, reloaded.EsiSyncState); // a transient error must not drop the coupling
        Assert.Equal(999L, reloaded.EsiFleetId);
    }

    [Fact]
    public async Task SyncFleet_WhenPersistentNonNotFoundFailure_UncouplesAfterThreshold()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        const int threshold = 60; // mirrors EsiFleetSyncService.DecoupleAfterPersistentFailures (~5 min at the 5s poll)
        var fleetId = await repository.AddAsync(new FleetEntity { Name = "Doctrine", CreatorCharacterId = owner, State = FleetState.Active }, ct);
        var fleet = (await repository.GetAsync(fleetId, ct))!;
        fleet.EsiFleetId = 999;
        fleet.EsiFleetBossId = owner;
        fleet.EsiSyncState = EsiFleetSyncState.Linked;
        await repository.UpdateAsync(fleet, ct);

        // A fleet that is gone but answers with a persistent 500 (not 404) never trips the fast NotFound path.
        var fake = new FakeFleetClient { MembersError = EsiError.Of(EsiErrorKind.ServerError, "Unhandled internal error encountered!", 500) };
        var service = new EsiFleetSyncService(fake, repository, registry, new NullSessionStore(), new RecordingFleetTransportClient(), bus,
            new FleetRosterChangeNotifier(new RecordingToastService(), new FakeExternalLookup()), new EsiAvailabilityState(), NullLogger<EsiFleetSyncService>.Instance);

        for (var poll = 1; poll < threshold; poll++)
            await service.SyncFleetAsync(fleet, ct);
        Assert.Equal(EsiFleetSyncState.Linked, (await repository.GetAsync(fleetId, ct))!.EsiSyncState); // a long-but-sub-threshold outage keeps the link

        await service.SyncFleetAsync(fleet, ct); // threshold-th consecutive failure → uncouple a fleet that never recovers

        var reloaded = (await repository.GetAsync(fleetId, ct))!;
        Assert.Equal(EsiFleetSyncState.NotLinked, reloaded.EsiSyncState);
        Assert.Null(reloaded.EsiFleetId);
    }

    [Fact]
    public async Task SyncFleet_WhenPersistentLocalAuthFailure_KeepsTheLink()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await repository.AddAsync(new FleetEntity { Name = "Doctrine", CreatorCharacterId = owner, State = FleetState.Active }, ct);
        var fleet = (await repository.GetAsync(fleetId, ct))!;
        fleet.EsiFleetId = 999;
        fleet.EsiFleetBossId = owner;
        fleet.EsiSyncState = EsiFleetSyncState.Linked;
        await repository.UpdateAsync(fleet, ct);

        // A local token problem (re-auth / clock skew surfaced as AuthRequired) says nothing about whether the in-game
        // fleet still exists, so it must never uncouple — not even after a run well past the server-side threshold.
        var fake = new FakeFleetClient { MembersError = EsiError.Of(EsiErrorKind.AuthRequired, "needs re-auth", 401) };
        var service = new EsiFleetSyncService(fake, repository, registry, new NullSessionStore(), new RecordingFleetTransportClient(), bus,
            new FleetRosterChangeNotifier(new RecordingToastService(), new FakeExternalLookup()), new EsiAvailabilityState(), NullLogger<EsiFleetSyncService>.Instance);

        for (var poll = 0; poll < 65; poll++) // well past DecoupleAfterPersistentFailures
            await service.SyncFleetAsync(fleet, ct);

        Assert.Equal(EsiFleetSyncState.Linked, (await repository.GetAsync(fleetId, ct))!.EsiSyncState);
    }

    [Fact]
    public async Task SyncFleet_WhenNonNotFoundFailureBrokenBySuccess_KeepsTheLink()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await repository.AddAsync(new FleetEntity { Name = "Doctrine", CreatorCharacterId = owner, State = FleetState.Active }, ct);
        await repository.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = owner, WingId = -1, SquadId = -1 }, ct);
        var fleet = (await repository.GetAsync(fleetId, ct))!;
        fleet.EsiFleetId = 999;
        fleet.EsiFleetBossId = owner;
        fleet.EsiSyncState = EsiFleetSyncState.Linked;
        await repository.UpdateAsync(fleet, ct);

        var fake = new FakeFleetClient
        {
            Members = [new EsiFleetMember { CharacterId = owner }],
            MembersError = EsiError.Of(EsiErrorKind.ServerError, "Unhandled internal error encountered!", 500),
        };
        var service = new EsiFleetSyncService(fake, repository, registry, new NullSessionStore(), new RecordingFleetTransportClient(), bus,
            new FleetRosterChangeNotifier(new RecordingToastService(), new FakeExternalLookup()), new EsiAvailabilityState(), NullLogger<EsiFleetSyncService>.Instance);

        for (var poll = 0; poll < 59; poll++) // a long run of transient 500s...
            await service.SyncFleetAsync(fleet, ct);

        fake.MembersError = null; // ...then one good poll resets the streak
        await service.SyncFleetAsync(fleet, ct);

        fake.MembersError = EsiError.Of(EsiErrorKind.ServerError, "Unhandled internal error encountered!", 500);
        for (var poll = 0; poll < 59; poll++) // another long-but-sub-threshold run — total > 60, but never 60 consecutive
            await service.SyncFleetAsync(fleet, ct);

        Assert.Equal(EsiFleetSyncState.Linked, (await repository.GetAsync(fleetId, ct))!.EsiSyncState); // a success between blips never lets it uncouple
    }

    [Fact]
    public async Task SyncServerFleet_WhenBoss_MirrorsLiveRoster_DiffsServerPlan_AndBroadcasts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const string server = "https://srv:1";
        const int boss = 100;
        const long esiFleetId = 999;
        const long serverFleetId = 7;

        // The server stores+relays the EsiFleetId + the planned roster; the client does the ESI polling.
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[server] = [ServerCoupled(serverFleetId, esiFleetId, boss)];
        transport.MembersByFleet[serverFleetId] = [ServerMember(55, 100), ServerMember(56, 200), ServerMember(57, 300)];

        // Live in-game: 100 + 200 joined, 300 still missing, 999 in-game but off-plan.
        var live = new FakeFleetClient
        {
            Members = [new EsiFleetMember { CharacterId = 100 }, new EsiFleetMember { CharacterId = 200 }, new EsiFleetMember { CharacterId = 999 }],
        };

        FleetChangedEvent? captured = null;
        using var sub = bus.Subscribe<FleetChangedEvent>((evt, _) => { captured = evt; return Task.CompletedTask; });

        var service = new EsiFleetSyncService(live, repository, registry, new NullSessionStore(), transport, bus,
            new FleetRosterChangeNotifier(new RecordingToastService(), new FakeExternalLookup()), new EsiAvailabilityState(), NullLogger<EsiFleetSyncService>.Instance);

        await service.SyncServerFleetsForCharacterAsync(server, boss, ct);

        Assert.NotNull(captured);
        Assert.Equal(FleetChangeKind.RosterChanged, captured!.Data.Kind);
        Assert.Equal(serverFleetId, captured.FleetId); // keyed by the server fleet id so its open roster window refreshes
    }

    [Fact]
    public async Task SyncServerFleet_WhenNotBoss_IsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const string server = "https://srv:1";
        const int boss = 100;
        const int otherMember = 200;

        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[server] = [ServerCoupled(7, 999, boss)];
        transport.MembersByFleet[7] = [ServerMember(55, boss), ServerMember(56, otherMember)];

        var live = new FakeFleetClient { Members = [new EsiFleetMember { CharacterId = boss }] };
        FleetChangedEvent? captured = null;
        using var sub = bus.Subscribe<FleetChangedEvent>((evt, _) => { captured = evt; return Task.CompletedTask; });

        var service = new EsiFleetSyncService(live, repository, registry, new NullSessionStore(), transport, bus,
            new FleetRosterChangeNotifier(new RecordingToastService(), new FakeExternalLookup()), new EsiAvailabilityState(), NullLogger<EsiFleetSyncService>.Instance);

        await service.SyncServerFleetsForCharacterAsync(server, otherMember, ct); // we are a member, not the boss

        Assert.Null(captured); // the non-boss case is covered by member self-report, not the boss mirror
    }

    [Fact]
    public async Task SyncServerFleet_WhenServerUnreachable_SkipsQuietlyWithoutThrowingOrBroadcasting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const string server = "https://srv:1";
        var transport = new RecordingFleetTransportClient();
        transport.UnreachableServers.Add(server); // ListMyFleetsAsync throws FleetTransportException (server down)

        FleetChangedEvent? captured = null;
        using var sub = bus.Subscribe<FleetChangedEvent>((evt, _) => { captured = evt; return Task.CompletedTask; });

        var service = new EsiFleetSyncService(new FakeFleetClient(), repository, registry, new NullSessionStore(), transport, bus,
            new FleetRosterChangeNotifier(new RecordingToastService(), new FakeExternalLookup()), new EsiAvailabilityState(), NullLogger<EsiFleetSyncService>.Instance);

        // Was a 5s error-spam regression: an unreachable server must be swallowed (logged at Debug), not propagate.
        await service.SyncServerFleetsForCharacterAsync(server, 100, ct);

        Assert.Null(captured);
    }

    [Fact]
    public async Task SyncServerFleet_WhenInGameFleetGone_UncouplesViaRpc_AfterToleranceWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const string server = "https://srv:1";
        const int boss = 100;
        const long serverFleetId = 7;

        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[server] = [ServerCoupled(serverFleetId, 999, boss)];
        transport.MembersByFleet[serverFleetId] = [ServerMember(55, boss)];

        // The in-game fleet is gone: every roster read is a NotFound.
        var live = new FakeFleetClient { MembersError = EsiError.Of(EsiErrorKind.NotFound, "fleet not found", 404) };
        var service = new EsiFleetSyncService(live, repository, registry, new NullSessionStore(), transport, bus,
            new FleetRosterChangeNotifier(new RecordingToastService(), new FakeExternalLookup()), new EsiAvailabilityState(), NullLogger<EsiFleetSyncService>.Instance);

        // A brief blip must not uncouple: a second consecutive miss confirms it.
        await service.SyncServerFleetsForCharacterAsync(server, boss, ct);
        Assert.Empty(transport.UncoupleCalls); // still coupled after 1

        await service.SyncServerFleetsForCharacterAsync(server, boss, ct); // 2nd consecutive miss → uncouple

        var call = Assert.Single(transport.UncoupleCalls);
        Assert.Equal(server, call.ServerAddress);
        Assert.Equal(serverFleetId, call.FleetId);
        Assert.Equal(boss, call.ActingCharacterId); // the boss token clears the server-stored link
    }

    private static FleetInfo ServerCoupled(long fleetId, long? esiFleetId, int boss) =>
        new(fleetId, "Doctrine", null, FleetVisibility.Public, FleetState.Active, boss,
            null, null, System.DateTimeOffset.UtcNow, FleetActivation.Active, null, esiFleetId, boss);

    private static FleetMemberInfo ServerMember(long id, int characterId) =>
        new(id, characterId, -1, -1, FleetRole.SquadMember, false);

    private sealed class FakeFleetClient : IEsiFleetClient
    {
        public EsiFleetMember[] Members { get; set; } = [];

        /// <summary>When set, GetMembers returns this failure instead of <see cref="Members"/> (dissolve/transient tests).</summary>
        public EsiError? MembersError { get; set; }

        public Task<EsiResult<EsiCharacterFleet>> GetCharacterFleetAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiCharacterFleet>.Fail(EsiError.Of(EsiErrorKind.NotFound, "not used")));

        public Task<EsiResult<EsiFleetMember[]>> GetMembersAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(MembersError is { } error ? EsiResult<EsiFleetMember[]>.Fail(error) : EsiResult<EsiFleetMember[]>.Ok(Members));

        public Task<EsiResult<EsiFleetWing[]>> GetWingsAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiFleetWing[]>.Ok([]));

        public Task<EsiResult> SetFleetSettingsAsync(long fleetId, int actingCharacterId, string? motd, bool? isFreeMove,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> MoveMemberAsync(long fleetId, int memberCharacterId, string role, long? wingId, long? squadId,
            int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> KickMemberAsync(long fleetId, int memberCharacterId, int actingCharacterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult<long>> CreateWingAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<long>.Ok(0));

        public Task<EsiResult> RenameWingAsync(long fleetId, long wingId, string name, int actingCharacterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult<long>> CreateSquadAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<long>.Ok(0));

        public Task<EsiResult> RenameSquadAsync(long fleetId, long squadId, string name, int actingCharacterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> DeleteWingAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> DeleteSquadAsync(long fleetId, long squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> InviteMemberAsync(long fleetId, int characterId, string role, long? wingId, long? squadId,
            int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());
    }
}
