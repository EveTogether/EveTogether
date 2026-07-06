using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Stream G / G-3: dropping a dragged roster member resolves to a move (onto a position) or a swap (onto an occupied
/// commander slot). The drop-resolver is pure (node-only); <see cref="FleetRosterViewModel.HandleDropAsync"/>
/// applies it through the same move/swap seams as the right-click cascade (G-1 engine + G-2 transport). The drag
/// interaction itself is verified by hand (Avalonia headless can't drive a pointer-drag); this covers the logic under it.
/// </summary>
public class FleetRosterDropTests
{
    private const int Owner = 95000001;
    private const int CharA = 96000001;
    private const int CharB = 96000002;
    private const int CharC = 96000003;

    private sealed record Setup(
        IFleetRepository Repository, long FleetId, long WingId, long SquadId, FleetRosterViewModel Roster,
        FleetMemberInfo MemberA, FleetMemberInfo MemberB, FleetMemberInfo MemberC);

    // A fleet with one wing (CharA = wing commander), one squad in it (CharB = squad member), and CharC left unassigned.
    private static async Task<Setup> BuildAsync(TestClientInstance instance, bool isOwner = true)
    {
        var services = instance.Services;
        var characters = services.GetRequiredService<ICharacterRegistry>();
        foreach (var (id, name) in new[] { (Owner, "FC"), (CharA, "Alpha"), (CharB, "Bravo"), (CharC, "Charlie") })
            await characters.AddOrUpdateAsync(new Character(name, id));

        var service = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();

        var fleetId = (await service.CreateLocalFleetAsync("drop test", null, Owner)).Value;
        var wingId = (await service.AddWingAsync(fleetId, "Wing 1", Owner)).Value;
        var squadId = (await service.AddSquadAsync(wingId, "Squad 1", Owner)).Value;
        await service.AddExternalAsync(fleetId, CharA, Owner);
        await service.AddExternalAsync(fleetId, CharB, Owner);
        await service.AddExternalAsync(fleetId, CharC, Owner);

        var seeded = await repository.ListMembersAsync(fleetId);
        var a = seeded.First(m => m.CharacterId == CharA);
        var b = seeded.First(m => m.CharacterId == CharB);
        await service.MoveMemberAsync(a.Id, FleetRole.WingCommander, wingId, -1, Owner);
        await service.MoveMemberAsync(b.Id, FleetRole.SquadMember, wingId, squadId, Owner);

        var fleet = await repository.GetAsync(fleetId);
        var info = new FleetInfo(
            fleet!.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation);
        var client = new LocalFleetClient(service, repository, characters, Owner);
        var roster = new FleetRosterViewModel(services, client, info, isOwner, Owner);

        for (var i = 0; i < 100 && roster.Tree.Count == 0; i++)
            await Task.Delay(50);

        // The member infos a real drag carries come from the tree rows, not the repository entities.
        FleetMemberInfo Info(int characterId) =>
            AllMemberNodes(roster).First(n => n.Member.CharacterId == characterId).Member;
        return new Setup(repository, fleetId, wingId, squadId, roster, Info(CharA), Info(CharB), Info(CharC));
    }

    private static IEnumerable<MemberNodeViewModel> AllMemberNodes(FleetRosterViewModel roster)
    {
        // An unplaced member (e.g. CharC) has no structure node — their node hangs on the left-list entry.
        foreach (var entry in roster.Entries)
            if (entry.Node is { } listNode)
                yield return listNode;

        var root = Root(roster);
        if (root.Commander is { } fc) yield return fc;
        foreach (var child in root.Children)
        {
            if (child is MemberNodeViewModel rootMember) yield return rootMember;
            if (child is not WingNodeViewModel wing) continue;
            if (wing.Commander is { } wc) yield return wc;
            foreach (var wingChild in wing.Children)
            {
                if (wingChild is MemberNodeViewModel wingMember) yield return wingMember;
                if (wingChild is not SquadNodeViewModel squad) continue;
                if (squad.Commander is { } sc) yield return sc;
                foreach (var squadMember in squad.Members) yield return squadMember;
            }
        }
    }

    private static FleetRootNodeViewModel Root(FleetRosterViewModel roster) =>
        Assert.IsType<FleetRootNodeViewModel>(roster.Tree[0]);

    // The default fleet already ships with a wing + squad, so select by the ids this setup created (not First()).
    private static WingNodeViewModel Wing(Setup setup) =>
        Root(setup.Roster).Children.OfType<WingNodeViewModel>().First(w => w.WingId == setup.WingId);

    private static SquadNodeViewModel Squad(Setup setup) =>
        Wing(setup).Children.OfType<SquadNodeViewModel>().First(s => s.SquadId == setup.SquadId);

    [AvaloniaFact]
    public async Task Resolve_OntoOccupiedWingCommander_Swaps()
    {
        using var instance = TestClientInstance.Create();
        var setup = await BuildAsync(instance);

        // CharB (a squad member) dropped on the wing → the wing's commander (CharA) is occupied, so they swap.
        var resolution = RosterDropResolver.Resolve(setup.MemberB, Wing(setup));

        Assert.Equal(RosterDropAction.Swap, resolution.Action);
        Assert.Equal(setup.MemberA.Id, resolution.OtherMemberId);
    }

    [AvaloniaFact]
    public async Task Resolve_OntoSquad_MovesAsSquadMember()
    {
        using var instance = TestClientInstance.Create();
        var setup = await BuildAsync(instance);

        // CharC (unassigned) dropped on the squad → move them in as a squad member at that wing/squad.
        var resolution = RosterDropResolver.Resolve(setup.MemberC, Squad(setup));

        Assert.Equal(RosterDropAction.Move, resolution.Action);
        Assert.Equal(FleetRole.SquadMember, resolution.Role);
        Assert.Equal(setup.WingId, resolution.WingId);
        Assert.Equal(setup.SquadId, resolution.SquadId);
    }

    [AvaloniaFact]
    public async Task Resolve_OntoOwnCommanderSlot_IsNoOp()
    {
        using var instance = TestClientInstance.Create();
        var setup = await BuildAsync(instance);

        // CharA is already this wing's commander → dropping them on their own wing does nothing.
        var resolution = RosterDropResolver.Resolve(setup.MemberA, Wing(setup));

        Assert.Equal(RosterDropAction.None, resolution.Action);
    }

    [AvaloniaFact]
    public async Task Resolve_OntoPlainSquadMember_JoinsThatSquad()
    {
        using var instance = TestClientInstance.Create();
        var setup = await BuildAsync(instance);

        var squadMemberLeaf = Squad(setup).Members.First(m => m.Member.Id == setup.MemberB.Id);
        var resolution = RosterDropResolver.Resolve(setup.MemberC, squadMemberLeaf);

        Assert.Equal(RosterDropAction.Move, resolution.Action);
        Assert.Equal(FleetRole.SquadMember, resolution.Role);
        Assert.Equal(setup.SquadId, resolution.SquadId);
    }

    [AvaloniaFact]
    public async Task HandleDrop_OntoOccupiedCommander_SwapsPositionsEndToEnd()
    {
        using var instance = TestClientInstance.Create();
        var setup = await BuildAsync(instance);

        await setup.Roster.HandleDropAsync(setup.MemberB.Id, Wing(setup));

        var after = await setup.Repository.ListMembersAsync(setup.FleetId);
        var a = after.First(m => m.Id == setup.MemberA.Id);
        var b = after.First(m => m.Id == setup.MemberB.Id);
        // B took the wing-commander slot; A took B's old squad-member spot.
        Assert.Equal(FleetRole.WingCommander, b.Role);
        Assert.Equal(setup.WingId, b.WingId);
        Assert.Equal(FleetRole.SquadMember, a.Role);
        Assert.Equal(setup.SquadId, a.SquadId);
    }

    [AvaloniaFact]
    public async Task RosterWindow_WithDragHandlers_Renders()
    {
        using var instance = TestClientInstance.Create();
        var setup = await BuildAsync(instance);

        // Iron Law #9: the drag interaction itself is verified by hand, but the window must still construct and lay out
        // with the G-3 drag handlers attached. Render it headless and capture a frame.
        var window = new FleetRosterWindow(setup.Roster) { Width = 760, Height = 560 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-roster-drag.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task HandleDrop_ByNonOwner_DoesNothing()
    {
        using var instance = TestClientInstance.Create();
        var setup = await BuildAsync(instance, isOwner: false);

        await setup.Roster.HandleDropAsync(setup.MemberB.Id, Wing(setup));

        var after = await setup.Repository.ListMembersAsync(setup.FleetId);
        // Unchanged: A is still the wing commander, B still the squad member.
        Assert.Equal(FleetRole.WingCommander, after.First(m => m.Id == setup.MemberA.Id).Role);
        Assert.Equal(FleetRole.SquadMember, after.First(m => m.Id == setup.MemberB.Id).Role);
    }
}
