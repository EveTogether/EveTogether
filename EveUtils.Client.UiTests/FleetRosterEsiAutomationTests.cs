using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Auto Apply / Auto Invite. With the toggle on, a coupled boss client pushes structure/invites hands-off:
/// adding a wing pushes it to the live fleet, and a member assigned into structure is invited in-game — both behind
/// the same coupled + write_fleet gate as the manual buttons. With the toggle off neither fires. The toggles persist
/// per fleet through the storage-role seam. The ESI side is driven by a recording <see cref="IEsiFleetClient"/>; the
/// in-game effect is verified by hand (Iron Law #9).
/// </summary>
public class FleetRosterEsiAutomationTests
{
    private const int Owner = 95000010;
    private const int Recruit = 96000010;
    private const long EsiFleetId = 999;

    [AvaloniaFact]
    public async Task AutoInvite_On_WhenMemberAssignedIntoStructure_InvitesThatPilot()
    {
        var esi = new RecordingEsiFleetClient { Wings = LiveDefaultStructure() };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IEsiFleetClient>(esi));
        var setup = await BuildCoupledFleetAsync(instance);
        setup.Roster.EsiAutoInviteMembers = true;

        await setup.Roster.HandleDropAsync(setup.RecruitMemberId, SquadNode(setup));

        var invite = Assert.Single(esi.Invites); // exactly the dropped pilot, at the resolved live position
        Assert.Equal(Recruit, invite.Character);
        Assert.Equal(7001L, invite.WingId);
        Assert.Equal(8001L, invite.SquadId);
    }

    [AvaloniaFact]
    public async Task AutoInvite_Off_WhenMemberAssignedIntoStructure_DoesNotInvite()
    {
        var esi = new RecordingEsiFleetClient { Wings = LiveDefaultStructure() };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IEsiFleetClient>(esi));
        var setup = await BuildCoupledFleetAsync(instance);
        // EsiAutoInviteMembers stays false.

        await setup.Roster.HandleDropAsync(setup.RecruitMemberId, SquadNode(setup));

        Assert.Empty(esi.Invites);
    }

    [AvaloniaFact]
    public async Task AutoApply_On_WhenWingAdded_PushesTheNewWingToTheLiveFleet()
    {
        var esi = new RecordingEsiFleetClient { Wings = LiveDefaultStructure() };
        var dialog = new RecordingDialogService { OnPromptText = (_, _, _) => Task.FromResult<string?>("Logi") };
        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IEsiFleetClient>(esi);
            s.AddSingleton<IDialogService>(dialog);
        });
        var setup = await BuildCoupledFleetAsync(instance);
        setup.Roster.EsiAutoApplyStructure = true;

        await Root(setup.Roster).AddWingCommand.ExecuteAsync(null);

        // "Logi" isn't in the live fleet (only the default wing is) → Auto Apply creates it in-game.
        Assert.Contains(esi.RenamedWings, w => w.Name == "Logi");
        Assert.NotEmpty(esi.CreatedWings);
    }

    [AvaloniaFact]
    public async Task AutoApply_Off_WhenWingAdded_DoesNotPushToTheLiveFleet()
    {
        var esi = new RecordingEsiFleetClient { Wings = LiveDefaultStructure() };
        var dialog = new RecordingDialogService { OnPromptText = (_, _, _) => Task.FromResult<string?>("Logi") };
        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IEsiFleetClient>(esi);
            s.AddSingleton<IDialogService>(dialog);
        });
        var setup = await BuildCoupledFleetAsync(instance);
        // EsiAutoApplyStructure stays false.

        await Root(setup.Roster).AddWingCommand.ExecuteAsync(null);

        Assert.Empty(esi.CreatedWings);
    }

    [AvaloniaFact]
    public async Task TogglingAutoApply_PersistsTheFlagThroughTheSeam()
    {
        var esi = new RecordingEsiFleetClient { Wings = LiveDefaultStructure() };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IEsiFleetClient>(esi));
        var setup = await BuildCoupledFleetAsync(instance);
        var repository = instance.Services.GetRequiredService<IFleetRepository>();

        setup.Roster.EsiAutoApplyStructure = true;

        // The save is fire-and-forget from the property setter; poll the store until it lands.
        for (var i = 0; i < 100; i++)
        {
            if ((await repository.GetAsync(setup.FleetId))!.EsiAutoApplyStructure)
                break;
            await Task.Delay(20);
        }
        Assert.True((await repository.GetAsync(setup.FleetId))!.EsiAutoApplyStructure);
    }

    private sealed record Setup(long FleetId, long WingId, long SquadId, long RecruitMemberId, FleetRosterViewModel Roster);

    // A coupled local fleet owned by Owner (who holds read+write_fleet) with the default wing/squad and one unassigned
    // recruit ready to be dropped into structure. The fleet is coupled (EsiFleetId set, boss = Owner) so the ESI band's
    // write gate evaluates to enabled after refresh.
    private static async Task<Setup> BuildCoupledFleetAsync(TestClientInstance instance)
    {
        var services = instance.Services;
        var characters = services.GetRequiredService<ICharacterRegistry>();
        string[] scopes = [FleetsScopeCatalog.ReadFleet, FleetsScopeCatalog.WriteFleet];
        await characters.AddOrUpdateAsync(new Character("FC", Owner, GrantedScopes: scopes));
        await characters.AddOrUpdateAsync(new Character("Recruit", Recruit));

        var service = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();

        var fleetId = (await service.CreateLocalFleetAsync("auto test", null, Owner)).Value;
        var wingId = (await service.AddWingAsync(fleetId, "Wing 1", Owner)).Value;
        var squadId = (await service.AddSquadAsync(wingId, "Squad 1", Owner)).Value;
        await service.AddExternalAsync(fleetId, Recruit, Owner);
        var recruitMemberId = (await repository.ListMembersAsync(fleetId)).First(m => m.CharacterId == Recruit).Id;

        var fleet = (await repository.GetAsync(fleetId))!;
        var info = new FleetInfo(fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation,
            EsiFleetId: EsiFleetId, EsiFleetBossId: Owner);
        var client = new LocalFleetClient(service, repository, characters, Owner);
        var roster = new FleetRosterViewModel(services, client, info, isOwner: true, Owner);

        for (var i = 0; i < 100 && roster.Tree.Count == 0; i++)
            await Task.Delay(20);
        await roster.RefreshCommand.ExecuteAsync(null); // evaluate the ESI write scope so the gate is open
        return new Setup(fleetId, wingId, squadId, recruitMemberId, roster);
    }

    private static EsiFleetWing[] LiveDefaultStructure() =>
        [new EsiFleetWing { Id = 7001, Name = "Wing 1", Squads = [new EsiFleetSquadInfo { Id = 8001, Name = "Squad 1" }] }];

    private static FleetRootNodeViewModel Root(FleetRosterViewModel roster) =>
        Assert.IsType<FleetRootNodeViewModel>(roster.Tree[0]);

    private static SquadNodeViewModel SquadNode(Setup setup) =>
        Root(setup.Roster).Children.OfType<WingNodeViewModel>().First(w => w.WingId == setup.WingId)
            .Children.OfType<SquadNodeViewModel>().First(s => s.SquadId == setup.SquadId);

    private sealed class RecordingEsiFleetClient : IEsiFleetClient
    {
        public IReadOnlyList<EsiFleetWing> Wings { get; set; } = [];
        public IReadOnlyList<EsiFleetMember> LiveMembers { get; set; } = [];
        public long NextWingId { get; set; } = 5001;
        public long NextSquadId { get; set; } = 6001;
        public List<(long FleetId, long WingId)> CreatedWings { get; } = [];
        public List<(long FleetId, long WingId, long SquadId)> CreatedSquads { get; } = [];
        public List<(long WingId, string Name)> RenamedWings { get; } = [];
        public List<(long SquadId, string Name)> RenamedSquads { get; } = [];
        public List<(int Character, long? WingId, long? SquadId)> Invites { get; } = [];

        public Task<EsiResult<EsiCharacterFleet>> GetCharacterFleetAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiCharacterFleet>.Fail(EsiError.Of(EsiErrorKind.NotFound, "not used")));

        public Task<EsiResult<EsiFleetMember[]>> GetMembersAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiFleetMember[]>.Ok([.. LiveMembers]));

        public Task<EsiResult<EsiFleetWing[]>> GetWingsAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiFleetWing[]>.Ok([.. Wings]));

        public Task<EsiResult> SetFleetSettingsAsync(long fleetId, int actingCharacterId, string? motd, bool? isFreeMove, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> MoveMemberAsync(long fleetId, int memberCharacterId, string role, long? wingId, long? squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> KickMemberAsync(long fleetId, int memberCharacterId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult<long>> CreateWingAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default)
        {
            var id = NextWingId++;
            CreatedWings.Add((fleetId, id));
            return Task.FromResult(EsiResult<long>.Ok(id));
        }

        public Task<EsiResult> RenameWingAsync(long fleetId, long wingId, string name, int actingCharacterId, CancellationToken cancellationToken = default)
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

        public Task<EsiResult> RenameSquadAsync(long fleetId, long squadId, string name, int actingCharacterId, CancellationToken cancellationToken = default)
        {
            RenamedSquads.Add((squadId, name));
            return Task.FromResult(EsiResult.Ok());
        }

        public Task<EsiResult> DeleteWingAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> DeleteSquadAsync(long fleetId, long squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> InviteMemberAsync(long fleetId, int characterId, string role, long? wingId, long? squadId, int actingCharacterId, CancellationToken cancellationToken = default)
        {
            Invites.Add((characterId, wingId, squadId));
            return Task.FromResult(EsiResult.Ok());
        }
    }
}
