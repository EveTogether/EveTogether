using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Headless UI verification that roster mutations on a client-only fleet flow through to the
/// <see cref="FleetRosterViewModel"/> tree — the composition actions previously verified by hand (add wing/squad,
/// add external pilot, move member, delete empty wing). Each mutation goes through <see cref="ClientFleetService"/>
/// (no UI dialogs) and the tree is re-read via the public refresh command. All in-process: no server, no gRPC.
/// </summary>
public class ClientOnlyFleetMutationTests
{
    private const int Owner = 95000001;
    private const int SecondLocal = 95000002;
    private const int External = 96000001;

    [AvaloniaFact]
    public async Task AddWing_AppearsInRosterTree()
    {
        using var ctx = await RosterContext.CreateAsync(Owner);

        var added = await ctx.Service.AddWingAsync(ctx.FleetId, "Wing 2", Owner);
        Assert.True(added.IsSuccess);
        await ctx.ReloadAsync();

        var wings = ctx.Root.Children.OfType<WingNodeViewModel>().ToList();
        Assert.Equal(2, wings.Count);
        Assert.Contains(wings, w => w.Label.StartsWith("Wing 2"));
    }

    [AvaloniaFact]
    public async Task AddSquad_AppearsUnderWing()
    {
        using var ctx = await RosterContext.CreateAsync(Owner);

        var wing = ctx.Root.Children.OfType<WingNodeViewModel>().Single();
        var added = await ctx.Service.AddSquadAsync(wing.WingId, "Squad 2", Owner);
        Assert.True(added.IsSuccess);
        await ctx.ReloadAsync();

        var refreshedWing = ctx.Root.Children.OfType<WingNodeViewModel>().Single();
        var squads = refreshedWing.Children.OfType<SquadNodeViewModel>().ToList();
        Assert.Equal(2, squads.Count);
        Assert.Contains(squads, s => s.Label.StartsWith("Squad 2"));
    }

    [AvaloniaFact]
    public async Task AddExternalMember_IsFlaggedExternal_InMemberListButNotInStructure()
    {
        using var ctx = await RosterContext.CreateAsync(Owner);

        var added = await ctx.Service.AddExternalAsync(ctx.FleetId, External, Owner);
        Assert.True(added.IsSuccess);
        var member = await ctx.Repository.GetMemberAsync(added.Value);
        Assert.True(member is { IsExternal: true });

        await ctx.ReloadAsync();
        // A fresh external lands at fleet level without a position → left MEMBERS list only, with the manage
        // node so the owner can place or remove them; the STRUCTURE tree and its count show placed members only.
        var entry = ctx.Roster.Entries.Single(e => e.Member?.CharacterId == External);
        Assert.True(entry.CanManage);
        Assert.StartsWith("Fleet (1)", ctx.Root.Label);
        Assert.DoesNotContain(ctx.Root.Children, c => c is MemberNodeViewModel);
    }

    [AvaloniaFact]
    public async Task UnassignedMember_LeavesStructure_StaysManageableInMemberList()
    {
        using var ctx = await RosterContext.CreateAsync(Owner);
        var wing = ctx.Root.Children.OfType<WingNodeViewModel>().Single();
        var squad = wing.Children.OfType<SquadNodeViewModel>().Single();

        await ctx.Service.AddLocalCharacterAsync(ctx.FleetId, SecondLocal, Owner);
        var member = (await ctx.Repository.ListMembersAsync(ctx.FleetId)).Single(m => m.CharacterId == SecondLocal);
        await ctx.Service.MoveMemberAsync(member.Id, FleetRole.SquadMember, wing.WingId, squad.SquadId, Owner);
        await ctx.ReloadAsync();
        Assert.StartsWith("Fleet (2)", ctx.Root.Label); // placed → counted and in the tree

        // Unassign ("remove from squad", R3-5): the member must drop OUT of the structure — back to the left
        // MEMBERS list only — instead of dangling under the Fleet root as an "Unassigned" node.
        var moved = await ctx.Service.MoveMemberAsync(member.Id, FleetRole.Unassigned, -1, -1, Owner);
        Assert.True(moved.IsSuccess);
        await ctx.ReloadAsync();

        Assert.StartsWith("Fleet (1)", ctx.Root.Label);
        Assert.DoesNotContain(ctx.Root.Children, c => c is MemberNodeViewModel);
        var entry = ctx.Roster.Entries.Single(e => e.Member?.CharacterId == SecondLocal);
        Assert.Equal("unassigned", entry.Badge);
        Assert.True(entry.CanManage); // the left row carries the manage menu — the only surface left to place/kick them
    }

    [AvaloniaFact]
    public async Task MoveMember_ToSquadCommander_ShownAsSquadCommander()
    {
        using var ctx = await RosterContext.CreateAsync(Owner);
        var wing = ctx.Root.Children.OfType<WingNodeViewModel>().Single();
        var squad = wing.Children.OfType<SquadNodeViewModel>().Single();

        await ctx.Service.AddLocalCharacterAsync(ctx.FleetId, SecondLocal, Owner);
        var member = (await ctx.Repository.ListMembersAsync(ctx.FleetId)).Single(m => m.CharacterId == SecondLocal);

        var moved = await ctx.Service.MoveMemberAsync(
            member.Id, FleetRole.SquadCommander, wing.WingId, squad.SquadId, Owner);
        Assert.True(moved.IsSuccess);
        await ctx.ReloadAsync();

        var refreshedSquad = ctx.Root.Children.OfType<WingNodeViewModel>().Single()
            .Children.OfType<SquadNodeViewModel>().Single();
        Assert.Contains("SC:", refreshedSquad.Label);
    }

    [AvaloniaFact]
    public async Task DeleteEmptyWing_RemovedFromTree()
    {
        using var ctx = await RosterContext.CreateAsync(Owner);
        var added = await ctx.Service.AddWingAsync(ctx.FleetId, "Wing 2", Owner);
        Assert.True(added.IsSuccess);
        await ctx.ReloadAsync();
        Assert.Equal(2, ctx.Root.Children.OfType<WingNodeViewModel>().Count());

        var deleted = await ctx.Service.DeleteWingAsync(added.Value, Owner);
        Assert.True(deleted.IsSuccess);
        await ctx.ReloadAsync();

        var wings = ctx.Root.Children.OfType<WingNodeViewModel>().ToList();
        Assert.Single(wings);
        Assert.StartsWith("Wing 1", wings[0].Label);
    }

    /// <summary>
    /// Owns an isolated client instance + a created client-only fleet + a loaded roster view model, and exposes the
    /// current Fleet root node. Reloads are sequential (never concurrent with the constructor's initial load), so the
    /// best-effort name cache is written once per id.
    /// </summary>
    private sealed class RosterContext : IDisposable
    {
        private readonly TestClientInstance _instance;

        private RosterContext(TestClientInstance instance, ClientFleetService service, IFleetRepository repository,
            long fleetId, FleetRosterViewModel roster)
        {
            _instance = instance;
            Service = service;
            Repository = repository;
            FleetId = fleetId;
            Roster = roster;
        }

        public ClientFleetService Service { get; }
        public IFleetRepository Repository { get; }
        public long FleetId { get; }
        public FleetRosterViewModel Roster { get; }
        public FleetRootNodeViewModel Root => Assert.IsType<FleetRootNodeViewModel>(Roster.Tree[0]);

        public static async Task<RosterContext> CreateAsync(int owner)
        {
            var instance = TestClientInstance.Create();
            var services = instance.Services;
            var service = services.GetRequiredService<ClientFleetService>();
            var repository = services.GetRequiredService<IFleetRepository>();
            var characters = services.GetRequiredService<ICharacterRegistry>();

            var created = await service.CreateLocalFleetAsync("Mutation test", null, owner);
            Assert.True(created.IsSuccess);
            var fleet = await repository.GetAsync(created.Value);

            var info = new FleetInfo(
                fleet!.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
                fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation);
            var client = new LocalFleetClient(service, repository, characters, owner);
            var roster = new FleetRosterViewModel(services, client, info, isOwner: true, owner);

            await WaitForTreeAsync(roster);
            return new RosterContext(instance, service, repository, created.Value, roster);
        }

        public Task ReloadAsync() => Roster.RefreshCommand.ExecuteAsync(null);

        private static async Task WaitForTreeAsync(FleetRosterViewModel roster)
        {
            for (var i = 0; i < 100 && roster.Tree.Count == 0; i++)
                await Task.Delay(50);
        }

        public void Dispose() => _instance.Dispose();
    }
}
