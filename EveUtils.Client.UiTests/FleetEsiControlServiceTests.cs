using System;
using System.Collections.Generic;
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
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Client.UiTests;

/// <summary>
/// of the ESI fleet-coupling: the client orchestration that sets the live fleet's MOTD / free-move and
/// mirrors it locally. On a coupled fleet it writes via ESI, reflects the change on our internal fleet and broadcasts
/// a refresh; an uncoupled fleet fails before any ESI call. Driven over a real repository + event bus + a recording client.
/// </summary>
public class FleetEsiControlServiceTests
{
    [Fact]
    public async Task SetFleetSettings_OnCoupledFleet_WritesEsi_MirrorsLocally_AndBroadcasts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await repository.AddAsync(new FleetEntity { Name = "Doctrine", CreatorCharacterId = owner, State = FleetState.Active }, ct);
        var fleet = (await repository.GetAsync(fleetId, ct))!;
        fleet.EsiFleetId = 999;
        fleet.EsiFleetBossId = owner;
        fleet.EsiSyncState = EsiFleetSyncState.Linked;
        await repository.UpdateAsync(fleet, ct);

        var esi = new RecordingFleetClient();
        FleetChangedEvent? captured = null;
        using var sub = bus.Subscribe<FleetChangedEvent>((evt, _) => { captured = evt; return Task.CompletedTask; });

        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.SetFleetSettingsAsync(fleetId, esiFleetId: 999, bossCharacterId: owner, motd: "Form up on me", isFreeMove: true, ct);

        Assert.True(result.IsSuccess);
        Assert.NotNull(esi.LastSettings);
        Assert.Equal(999L, esi.LastSettings!.Value.FleetId);
        Assert.Equal(owner, esi.LastSettings.Value.Actor);
        Assert.Equal("Form up on me", esi.LastSettings.Value.Motd);
        Assert.True(esi.LastSettings.Value.IsFreeMove);

        var reloaded = (await repository.GetAsync(fleetId, ct))!;
        Assert.Equal("Form up on me", reloaded.Motd);
        Assert.True(reloaded.IsFreeMove);

        Assert.NotNull(captured);
        Assert.Equal(FleetChangeKind.RosterChanged, captured!.Data.Kind);
    }

    [Fact]
    public async Task SetFleetSettings_MirrorsLocallyOnlyWhenTheFleetIsInTheClientRepository()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        // A server fleet is not in the client repository; the ESI write still runs (the coupled ids are supplied) and the
        // missing local entity is simply not mirrored — this is the path that previously failed with "Fleet not found".
        const long serverFleetId = 4242;
        var esi = new RecordingFleetClient();
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.SetFleetSettingsAsync(serverFleetId, esiFleetId: 999, bossCharacterId: 100, motd: "Test", isFreeMove: true, ct);

        Assert.True(result.IsSuccess);
        Assert.NotNull(esi.LastSettings); // the ESI write ran against the supplied in-game ids
        Assert.Equal(999L, esi.LastSettings!.Value.FleetId);
    }

    [Fact]
    public async Task MoveMember_OnCoupledFleet_TranslatesInternalPositionToTheLiveEsiIdsByName()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        // No stored EsiWingId/EsiSquadId: the in-game ids are resolved from the live fleet by name.
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha() };
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.MoveMemberAsync(Fleets(instance.Services, repository, owner), fleetId, esiFleetId: 999, bossCharacterId: owner,
            memberCharacterId: 200, FleetRole.SquadMember, wingId, squadId, ct);

        Assert.True(result.IsSuccess);
        Assert.NotNull(esi.LastMove);
        Assert.Equal(999L, esi.LastMove!.Value.FleetId);
        Assert.Equal(200, esi.LastMove.Value.Member);
        Assert.Equal("squad_member", esi.LastMove.Value.Role);
        Assert.Equal(7001L, esi.LastMove.Value.WingId); // DPS resolved to the live ESI wing id by name
        Assert.Equal(8001L, esi.LastMove.Value.SquadId); // Alpha resolved to the live ESI squad id by name
        Assert.Equal(owner, esi.LastMove.Value.Actor);
    }

    [Fact]
    public async Task MoveMember_DoesNotBroadcast_TheInternalMoveAlreadyRefreshedTheRoster()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha() };
        FleetChangedEvent? captured = null;
        using var sub = bus.Subscribe<FleetChangedEvent>((evt, _) => { captured = evt; return Task.CompletedTask; });

        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.MoveMemberAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner,
            memberCharacterId: 200, FleetRole.SquadMember, wingId, squadId, ct);

        Assert.True(result.IsSuccess);
        Assert.Null(captured); // the ESI mirror is a side-effect of an already-broadcast internal move — it must not re-broadcast
    }

    [Fact]
    public async Task MoveMember_WhenTargetWingNotInTheLiveFleet_FailsWithoutMoving()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);

        var esi = new RecordingFleetClient(); // the live in-game fleet has no matching wing yet (structure not pushed)
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.MoveMemberAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner,
            memberCharacterId: 200, FleetRole.WingCommander, wingId, squadId: -1, ct);

        Assert.False(result.IsSuccess);
        Assert.Null(esi.LastMove); // the structure-not-pushed guard short-circuits before the ESI move
    }

    [Fact]
    public async Task KickMember_OnCoupledFleet_PushesEsiDelete_WithoutBroadcasting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        await SeedCoupledFleetAsync(repository, owner, ct);

        var esi = new RecordingFleetClient();
        FleetChangedEvent? captured = null;
        using var sub = bus.Subscribe<FleetChangedEvent>((evt, _) => { captured = evt; return Task.CompletedTask; });

        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.KickMemberAsync(esiFleetId: 999, bossCharacterId: owner, memberCharacterId: 200, ct);

        Assert.True(result.IsSuccess);
        Assert.NotNull(esi.LastKick);
        Assert.Equal(999L, esi.LastKick!.Value.FleetId);
        Assert.Equal(200, esi.LastKick.Value.Member);
        Assert.Equal(owner, esi.LastKick.Value.Actor);
        Assert.Null(captured); // kick mirrors the internal removal to ESI only — no re-broadcast
    }

    [Fact]
    public async Task RenameWing_FindsTheLiveWingByOldName_AndRenamesItToTheNewName()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha() }; // live wing still carries the old name "DPS"
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.RenameWingAsync(esiFleetId: 999, bossCharacterId: 100, oldName: "DPS", newName: "Logi", ct);

        Assert.True(result.IsSuccess);
        Assert.Contains(esi.RenamedWings, w => w.WingId == 7001L && w.Name == "Logi"); // located by old name, renamed to new
    }

    [Fact]
    public async Task RenameWing_WhenOldNameNotInTheLiveFleet_IsANoOpSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha() };
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.RenameWingAsync(999, bossCharacterId: 100, oldName: "Tackle", newName: "Logi", ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(esi.RenamedWings); // already renamed in-game / structure not pushed → nothing to do
    }

    [Fact]
    public async Task RenameSquad_FindsTheLiveSquadByOldNameWithinItsWing_AndRenamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha() }; // wing "DPS" → squad "Alpha" (id 8001)
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.RenameSquadAsync(999, bossCharacterId: 100, wingName: "DPS", oldSquadName: "Alpha", newName: "Guardians", ct);

        Assert.True(result.IsSuccess);
        Assert.Contains(esi.RenamedSquads, s => s.SquadId == 8001L && s.Name == "Guardians");
    }

    [Fact]
    public async Task DeleteObsolete_RemovesEmptyOutOfPlanUnits_ProtectsDefaults_AndSkipsOccupied()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        var esi = new RecordingFleetClient
        {
            Wings =
            [
                new EsiFleetWing { Id = 7001, Name = "DPS", Squads = [new EsiFleetSquadInfo { Id = 8001, Name = "Alpha" }] },   // in plan → keep
                new EsiFleetWing { Id = 7002, Name = "Tackle", Squads = [new EsiFleetSquadInfo { Id = 8002, Name = "Bravo" }] }, // out of plan, empty → remove
                new EsiFleetWing { Id = 7003, Name = "Wing 1", Squads = [new EsiFleetSquadInfo { Id = 8003, Name = "Squad 1" }] }, // EVE default → protect
                new EsiFleetWing { Id = 7004, Name = "Logi", Squads = [new EsiFleetSquadInfo { Id = 8004, Name = "Charlie" }] },  // out of plan but occupied → keep
            ],
            LiveMembers = [new EsiFleetMember { CharacterId = 200, WingId = 7004, SquadId = 8004 }],
        };
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.DeleteObsoleteInGameUnitsAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner, dryRun: false, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([8002L], esi.DeletedSquads);             // only the empty out-of-plan squad
        Assert.Equal([7002L], esi.DeletedWings);              // only the empty out-of-plan wing
        Assert.DoesNotContain(8003L, esi.DeletedSquads);      // EVE-default "Squad 1" protected
        Assert.DoesNotContain(7003L, esi.DeletedWings);       // EVE-default "Wing 1" protected
        Assert.DoesNotContain(7004L, esi.DeletedWings);       // occupied wing kept
    }

    [Fact]
    public async Task DeleteObsolete_DryRun_ListsWhatWouldBeRemoved_WithoutDeleting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        var esi = new RecordingFleetClient
        {
            Wings =
            [
                new EsiFleetWing { Id = 7001, Name = "DPS", Squads = [new EsiFleetSquadInfo { Id = 8001, Name = "Alpha" }] },
                new EsiFleetWing { Id = 7002, Name = "Tackle", Squads = [new EsiFleetSquadInfo { Id = 8002, Name = "Bravo" }] },
            ],
        };
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.DeleteObsoleteInGameUnitsAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner, dryRun: true, ct);

        Assert.True(result.IsSuccess);
        Assert.Contains("squad 'Tackle / Bravo'", result.Value!);
        Assert.Contains("wing 'Tackle'", result.Value!);
        Assert.Empty(esi.DeletedWings);  // dry run touches nothing
        Assert.Empty(esi.DeletedSquads);
    }

    [Fact]
    public async Task DeleteObsolete_WhenLiveMatchesThePlan_RemovesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha() };
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.DeleteObsoleteInGameUnitsAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner, dryRun: false, ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
        Assert.Empty(esi.DeletedWings);
        Assert.Empty(esi.DeletedSquads);
    }

    [Fact]
    public async Task ApplyFleetStructure_CreatesWingsAndSquadsMissingFromTheLiveFleet_AndBroadcasts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        var esi = new RecordingFleetClient(); // the live in-game fleet has no wings yet → both get created
        FleetChangedEvent? captured = null;
        using var sub = bus.Subscribe<FleetChangedEvent>((evt, _) => { captured = evt; return Task.CompletedTask; });

        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.ApplyFleetStructureAsync(Fleets(instance.Services, repository, owner), fleetId, esiFleetId: 999, bossCharacterId: owner, ct);

        Assert.True(result.IsSuccess);
        Assert.Single(esi.CreatedWings);
        Assert.Single(esi.CreatedSquads);
        Assert.Equal(5001L, esi.CreatedSquads[0].WingId); // the squad is created under the just-created wing's ESI id
        Assert.Contains(esi.RenamedWings, w => w.Name == "DPS");
        Assert.Contains(esi.RenamedSquads, s => s.Name == "Alpha");
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task ApplyFleetStructure_IsIdempotent_SkippingUnitsAlreadyInTheLiveFleetByName()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        // The live in-game fleet already has a wing + squad with the same names → match by name, re-create nothing.
        var esi = new RecordingFleetClient
        {
            Wings = [new EsiFleetWing { Id = 7001, Name = "DPS", Squads = [new EsiFleetSquadInfo { Id = 8001, Name = "Alpha" }] }],
        };
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.ApplyFleetStructureAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner, ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(esi.CreatedWings); // already present in-game → nothing re-created
        Assert.Empty(esi.CreatedSquads);
    }

    [Fact]
    public async Task InviteRoster_InvitesPlannedMembers_SkipsExternalAndAlreadyPresent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);
        foreach (var id in new[] { 200, 300, 500 })
            await repository.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = id, Role = FleetRole.SquadMember, WingId = wingId, SquadId = squadId }, ct);
        await repository.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = 400, Role = FleetRole.SquadMember, WingId = wingId, SquadId = squadId, IsExternal = true }, ct);

        // 500 is already in the live fleet → skipped; the in-game wing/squad ids are resolved from the live fleet by name.
        var esi = new RecordingFleetClient { LiveMembers = [new EsiFleetMember { CharacterId = 500 }], Wings = LiveDpsAlpha() };
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.InviteRosterAsync(Fleets(instance.Services, repository, owner), fleetId, esiFleetId: 999, bossCharacterId: owner, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count); // 400 (external) + 500 (present) skipped
        Assert.Contains(result.Value, o => o is { CharacterId: 200, Status: EsiInviteStatus.Invited });
        Assert.Contains(result.Value, o => o is { CharacterId: 300, Status: EsiInviteStatus.Invited });
        Assert.Contains(esi.Invites, i => i.Character == 200 && i.Role == "squad_member" && i.WingId == 7001L && i.SquadId == 8001L);
        Assert.DoesNotContain(esi.Invites, i => i.Character == 400);
        Assert.DoesNotContain(esi.Invites, i => i.Character == 500);
    }

    [Fact]
    public async Task InviteRoster_WhenInviteRejected_MarksFailed_AndContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);
        foreach (var id in new[] { 200, 300 })
            await repository.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = id, Role = FleetRole.SquadMember, WingId = wingId, SquadId = squadId }, ct);

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha() };
        esi.FailInviteFor.Add(200); // e.g. a CSPA charge blocks this pilot
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.InviteRosterAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner, ct);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value!, o => o.CharacterId == 200 && o.Status == EsiInviteStatus.Failed && o.Message is not null);
        Assert.Contains(result.Value!, o => o is { CharacterId: 300, Status: EsiInviteStatus.Invited });
        Assert.Equal(2, esi.Invites.Count); // a rejected invite doesn't stop the rest
    }

    [Fact]
    public async Task InviteRoster_WhenTargetOffline_SurfacesClearReason_NotRawRateLimitStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);
        await repository.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = 200, Role = FleetRole.SquadMember, WingId = wingId, SquadId = squadId }, ct);

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha() };
        esi.OfflineInviteFor.Add(200); // ESI answers the invite with a 420 (offline target), not the error limiter
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.InviteRosterAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner, ct);

        Assert.True(result.IsSuccess);
        var outcome = Assert.Single(result.Value!);
        Assert.Equal(EsiInviteStatus.Failed, outcome.Status);
        Assert.Equal("Pilot must be online in EVE to receive a fleet invite.", outcome.Message);
    }

    [Fact]
    public async Task InviteRoster_WhenTargetWingNotInTheLiveFleet_MarksFailedWithoutInviting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);
        await repository.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = 200, Role = FleetRole.SquadMember, WingId = wingId, SquadId = squadId }, ct);

        var esi = new RecordingFleetClient(); // the live in-game fleet has no matching wing yet (structure not pushed)
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.InviteRosterAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner, ct);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value!, o => o.CharacterId == 200 && o.Status == EsiInviteStatus.Failed);
        Assert.Empty(esi.Invites); // no ESI invite is sent for a pilot whose position can't be resolved
    }

    [Fact]
    public async Task InviteMember_AtResolvedInStructurePosition_InvitesThatSinglePilotByName()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha() };
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.InviteMemberAsync(Fleets(instance.Services, repository, owner), fleetId, esiFleetId: 999, bossCharacterId: owner,
            characterId: 200, FleetRole.SquadMember, wingId, squadId, ct);

        Assert.True(result.IsSuccess);
        var invite = Assert.Single(esi.Invites); // exactly the one pilot, not the whole roster
        Assert.Equal(200, invite.Character);
        Assert.Equal("squad_member", invite.Role);
        Assert.Equal(7001L, invite.WingId);  // DPS resolved to the live ESI wing id by name
        Assert.Equal(8001L, invite.SquadId); // Alpha resolved to the live ESI squad id by name
    }

    [Fact]
    public async Task InviteMember_WhenAlreadyInTheLiveFleet_IsNoOpSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha(), LiveMembers = [new EsiFleetMember { CharacterId = 200 }] };
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.InviteMemberAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner,
            characterId: 200, FleetRole.SquadMember, wingId, squadId, ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(esi.Invites); // the pilot is already in the in-game fleet → idempotent skip
    }

    [Fact]
    public async Task InviteMember_WhenWingNotInTheLiveFleet_FailsWithoutInviting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        var esi = new RecordingFleetClient(); // the live in-game fleet has no matching wing yet (structure not pushed)
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.InviteMemberAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner,
            characterId: 200, FleetRole.SquadMember, wingId, squadId, ct);

        Assert.False(result.IsSuccess);
        Assert.Empty(esi.Invites); // no invite for a pilot whose wing/squad isn't pushed in-game yet
    }

    [Fact]
    public async Task SyncMemberPosition_WhenPilotInLiveFleet_MovesThem_WithoutInviting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        // Pilot 200 is already in the live fleet → move them to the position; never an invite.
        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha(), LiveMembers = [new EsiFleetMember { CharacterId = 200 }] };
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.SyncMemberPositionAsync(Fleets(instance.Services, repository, owner), fleetId, esiFleetId: 999, bossCharacterId: owner,
            characterId: 200, FleetRole.SquadMember, wingId, squadId, invite: true, ct);

        Assert.True(result.IsSuccess);
        Assert.NotNull(esi.LastMove);
        Assert.Equal(200, esi.LastMove!.Value.Member);
        Assert.Empty(esi.Invites);
    }

    [Fact]
    public async Task SyncMemberPosition_WhenPilotNotInLiveFleet_AndInviteOn_InvitesThem_WithoutMoving()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha() }; // LiveMembers empty → 200 is not in the fleet
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.SyncMemberPositionAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner,
            characterId: 200, FleetRole.SquadMember, wingId, squadId, invite: true, ct);

        Assert.True(result.IsSuccess);
        Assert.Null(esi.LastMove);              // never a move on a non-member (no "Cannot move non-member" 400)
        var invite = Assert.Single(esi.Invites);
        Assert.Equal(200, invite.Character);
    }

    [Fact]
    public async Task SyncMemberPosition_WhenPilotNotInLiveFleet_AndInviteOff_DoesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        const int owner = 100;
        var fleetId = await SeedCoupledFleetAsync(repository, owner, ct);
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = fleetId, Name = "DPS" }, ct);
        var squadId = await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Alpha" }, ct);

        var esi = new RecordingFleetClient { Wings = LiveDpsAlpha() }; // absent
        var service = new FleetEsiControlService(esi, repository, bus);
        var result = await service.SyncMemberPositionAsync(Fleets(instance.Services, repository, owner), fleetId, 999, owner,
            characterId: 200, FleetRole.SquadMember, wingId, squadId, invite: false, ct);

        Assert.True(result.IsSuccess);
        Assert.Null(esi.LastMove);  // the key guard: no doomed move on a non-member
        Assert.Empty(esi.Invites);  // invite off → nothing happens
    }

    private static EsiFleetWing[] LiveDpsAlpha() =>
        [new EsiFleetWing { Id = 7001, Name = "DPS", Squads = [new EsiFleetSquadInfo { Id = 8001, Name = "Alpha" }] }];

    private static async Task<long> SeedCoupledFleetAsync(IFleetRepository repository, int owner, CancellationToken ct)
    {
        var fleetId = await repository.AddAsync(new FleetEntity { Name = "Doctrine", CreatorCharacterId = owner, State = FleetState.Active }, ct);
        var fleet = (await repository.GetAsync(fleetId, ct))!;
        fleet.EsiFleetId = 999;
        fleet.EsiFleetBossId = owner;
        fleet.EsiSyncState = EsiFleetSyncState.Linked;
        await repository.UpdateAsync(fleet, ct);
        return fleetId;
    }

    // The fleet-plan source the service reads through (works for client-only and server fleets); here it reads the seeded
    // client repository, the same way a LocalFleetClient does in the app.
    private static LocalFleetClient Fleets(IServiceProvider services, IFleetRepository repository, int owner) =>
        new(services.GetRequiredService<ClientFleetService>(), repository,
            services.GetRequiredService<ICharacterRegistry>(), owner);

    private sealed class RecordingFleetClient : IEsiFleetClient
    {
        public (long FleetId, int Actor, string? Motd, bool? IsFreeMove)? LastSettings { get; private set; }
        public (long FleetId, int Member, string Role, long? WingId, long? SquadId, int Actor)? LastMove { get; private set; }
        public (long FleetId, int Member, int Actor)? LastKick { get; private set; }
        public long NextWingId { get; set; } = 5001;
        public long NextSquadId { get; set; } = 6001;
        public List<(long FleetId, long WingId)> CreatedWings { get; } = [];
        public List<(long FleetId, long WingId, long SquadId)> CreatedSquads { get; } = [];
        public List<(long WingId, string Name)> RenamedWings { get; } = [];
        public List<(long SquadId, string Name)> RenamedSquads { get; } = [];
        public List<(long FleetId, int Character, string Role, long? WingId, long? SquadId, int Actor)> Invites { get; } = [];
        public HashSet<int> FailInviteFor { get; } = [];
        public HashSet<int> OfflineInviteFor { get; } = [];
        public List<long> DeletedWings { get; } = [];
        public List<long> DeletedSquads { get; } = [];
        public IReadOnlyList<EsiFleetMember> LiveMembers { get; set; } = [];

        public Task<EsiResult<EsiCharacterFleet>> GetCharacterFleetAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiCharacterFleet>.Fail(EsiError.Of(EsiErrorKind.NotFound, "not used")));

        public Task<EsiResult<EsiFleetMember[]>> GetMembersAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiFleetMember[]>.Ok([.. LiveMembers]));

        public IReadOnlyList<EsiFleetWing> Wings { get; set; } = [];

        public Task<EsiResult<EsiFleetWing[]>> GetWingsAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiFleetWing[]>.Ok([.. Wings]));

        public Task<EsiResult> SetFleetSettingsAsync(long fleetId, int actingCharacterId, string? motd, bool? isFreeMove,
            CancellationToken cancellationToken = default)
        {
            LastSettings = (fleetId, actingCharacterId, motd, isFreeMove);
            return Task.FromResult(EsiResult.Ok());
        }

        public Task<EsiResult> MoveMemberAsync(long fleetId, int memberCharacterId, string role, long? wingId, long? squadId,
            int actingCharacterId, CancellationToken cancellationToken = default)
        {
            LastMove = (fleetId, memberCharacterId, role, wingId, squadId, actingCharacterId);
            return Task.FromResult(EsiResult.Ok());
        }

        public Task<EsiResult> KickMemberAsync(long fleetId, int memberCharacterId, int actingCharacterId,
            CancellationToken cancellationToken = default)
        {
            LastKick = (fleetId, memberCharacterId, actingCharacterId);
            return Task.FromResult(EsiResult.Ok());
        }

        public Task<EsiResult<long>> CreateWingAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default)
        {
            var id = NextWingId++;
            CreatedWings.Add((fleetId, id));
            return Task.FromResult(EsiResult<long>.Ok(id));
        }

        public Task<EsiResult> RenameWingAsync(long fleetId, long wingId, string name, int actingCharacterId,
            CancellationToken cancellationToken = default)
        {
            RenamedWings.Add((wingId, name));
            return Task.FromResult(EsiResult.Ok());
        }

        public Task<EsiResult<long>> CreateSquadAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default)
        {
            var id = NextSquadId++;
            CreatedSquads.Add((fleetId, wingId, id));
            return Task.FromResult(EsiResult<long>.Ok(id));
        }

        public Task<EsiResult> RenameSquadAsync(long fleetId, long squadId, string name, int actingCharacterId,
            CancellationToken cancellationToken = default)
        {
            RenamedSquads.Add((squadId, name));
            return Task.FromResult(EsiResult.Ok());
        }

        public Task<EsiResult> DeleteWingAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default)
        {
            DeletedWings.Add(wingId);
            return Task.FromResult(EsiResult.Ok());
        }

        public Task<EsiResult> DeleteSquadAsync(long fleetId, long squadId, int actingCharacterId, CancellationToken cancellationToken = default)
        {
            DeletedSquads.Add(squadId);
            return Task.FromResult(EsiResult.Ok());
        }

        public Task<EsiResult> InviteMemberAsync(long fleetId, int characterId, string role, long? wingId, long? squadId,
            int actingCharacterId, CancellationToken cancellationToken = default)
        {
            Invites.Add((fleetId, characterId, role, wingId, squadId, actingCharacterId));
            if (OfflineInviteFor.Contains(characterId))
                // The offline-target shape ESI actually returns on this endpoint: a 420 without the error-limit headers.
                return Task.FromResult(EsiResult.Fail(EsiError.Of(EsiErrorKind.RateLimited, "ESI POST /fleets/999/members/ returned 420.", 420)));
            return Task.FromResult(FailInviteFor.Contains(characterId)
                ? EsiResult.Fail(EsiError.Of(EsiErrorKind.BadRequest, "Invite blocked (e.g. CSPA charge).", 520))
                : EsiResult.Ok());
        }
    }
}
