using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Stream G / G-1: SwapMembersCommand exchanges two members' exact roster positions (role + wing + squad) — the engine
/// behind dragging a pilot onto an occupied commander slot. Creator-only; both members must be in the same fleet.
/// </summary>
public class FleetMemberSwapTests
{
    private const int Owner = 95000001;
    private const int CharA = 96000001;
    private const int CharB = 96000002;

    private static async Task SeedAsync(IServiceProvider services, int id, string name) =>
        await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, id));

    [AvaloniaFact]
    public async Task Swap_ExchangesTheTwoMembersExactPositions()
    {
        using var instance = TestClientInstance.Create();
        await SeedAsync(instance.Services, Owner, "FC");
        var fleetService = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();

        var fleetId = (await fleetService.CreateLocalFleetAsync("swap test", null, Owner)).Value;
        var wingId = (await fleetService.AddWingAsync(fleetId, "Wing 1", Owner)).Value;
        var squadId = (await fleetService.AddSquadAsync(wingId, "Squad 1", Owner)).Value;
        Assert.True((await fleetService.AddExternalAsync(fleetId, CharA, Owner)).IsSuccess);
        Assert.True((await fleetService.AddExternalAsync(fleetId, CharB, Owner)).IsSuccess);

        var seeded = await repository.ListMembersAsync(fleetId);
        var a = seeded.First(m => m.CharacterId == CharA);
        var b = seeded.First(m => m.CharacterId == CharB);

        // A is the wing commander, B a plain squad member.
        Assert.True((await fleetService.MoveMemberAsync(a.Id, FleetRole.WingCommander, wingId, -1, Owner)).IsSuccess);
        Assert.True((await fleetService.MoveMemberAsync(b.Id, FleetRole.SquadMember, wingId, squadId, Owner)).IsSuccess);

        Assert.True((await fleetService.SwapMembersAsync(a.Id, b.Id, Owner)).IsSuccess);

        var after = await repository.ListMembersAsync(fleetId);
        var aAfter = after.First(m => m.Id == a.Id);
        var bAfter = after.First(m => m.Id == b.Id);

        // A took B's old squad-member spot; B took A's old wing-commander spot.
        Assert.Equal(FleetRole.SquadMember, aAfter.Role);
        Assert.Equal(squadId, aAfter.SquadId);
        Assert.Equal(wingId, aAfter.WingId);
        Assert.Equal(FleetRole.WingCommander, bAfter.Role);
        Assert.Equal(wingId, bAfter.WingId);
        Assert.Equal(-1, bAfter.SquadId);
    }

    [AvaloniaFact]
    public async Task Swap_ByANonOwner_IsDenied()
    {
        using var instance = TestClientInstance.Create();
        await SeedAsync(instance.Services, Owner, "FC");
        var fleetService = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();

        var fleetId = (await fleetService.CreateLocalFleetAsync("swap test", null, Owner)).Value;
        var wingId = (await fleetService.AddWingAsync(fleetId, "Wing 1", Owner)).Value;
        await fleetService.AddExternalAsync(fleetId, CharA, Owner);
        var members = await repository.ListMembersAsync(fleetId);
        var a = members.First(m => m.CharacterId == CharA);
        var owner = members.First(m => m.CharacterId == Owner);
        await fleetService.MoveMemberAsync(a.Id, FleetRole.WingCommander, wingId, -1, Owner);

        // CharB is not the creator → not allowed to manage the roster.
        Assert.False((await fleetService.SwapMembersAsync(a.Id, owner.Id, CharB)).IsSuccess);
    }

    [AvaloniaFact]
    public async Task Swap_OfMembersInDifferentFleets_IsDenied()
    {
        using var instance = TestClientInstance.Create();
        await SeedAsync(instance.Services, Owner, "FC");
        var fleetService = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();

        var fleetOne = (await fleetService.CreateLocalFleetAsync("fleet one", null, Owner)).Value;
        var fleetTwo = (await fleetService.CreateLocalFleetAsync("fleet two", null, Owner)).Value;
        var memberOne = (await repository.ListMembersAsync(fleetOne)).Single();
        var memberTwo = (await repository.ListMembersAsync(fleetTwo)).Single();

        Assert.False((await fleetService.SwapMembersAsync(memberOne.Id, memberTwo.Id, Owner)).IsSuccess);
    }
}
