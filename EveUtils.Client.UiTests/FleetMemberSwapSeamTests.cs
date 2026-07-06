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
/// Stream G / G-2: the <see cref="IFleetClient"/> swap seam routes to the right backend — a server-backed fleet hands
/// the swap to the gRPC transport, a client-only fleet runs the Shared <c>SwapMembersCommand</c> locally. G-1 already
/// proves the command's position-exchange semantics; this covers the new server/local plumbing on top of it.
/// </summary>
public class FleetMemberSwapSeamTests
{
    private const string Server = "https://eve.example";
    private const int Owner = 95000001;
    private const int CharA = 96000001;
    private const int CharB = 96000002;

    [AvaloniaFact]
    public async Task ServerFleetClient_Swap_RoutesToTheTransport()
    {
        var transport = new RecordingFleetTransportClient();
        var client = new ServerFleetClient(transport, Server, Owner);

        var (ok, _) = await client.SwapMembersAsync(41, 42);

        Assert.True(ok);
        Assert.Equal((41L, 42L, Owner), transport.LastSwap);
    }

    [AvaloniaFact]
    public async Task LocalFleetClient_Swap_ExchangesPositionsEndToEnd()
    {
        using var instance = TestClientInstance.Create();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("FC", Owner));
        var fleetService = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();
        var client = new LocalFleetClient(fleetService, repository, characters, Owner);

        var fleetId = (await fleetService.CreateLocalFleetAsync("swap seam", null, Owner)).Value;
        var wingId = (await fleetService.AddWingAsync(fleetId, "Wing 1", Owner)).Value;
        var squadId = (await fleetService.AddSquadAsync(wingId, "Squad 1", Owner)).Value;
        await fleetService.AddExternalAsync(fleetId, CharA, Owner);
        await fleetService.AddExternalAsync(fleetId, CharB, Owner);

        var seeded = await repository.ListMembersAsync(fleetId);
        var a = seeded.First(m => m.CharacterId == CharA);
        var b = seeded.First(m => m.CharacterId == CharB);
        await fleetService.MoveMemberAsync(a.Id, FleetRole.WingCommander, wingId, -1, Owner);
        await fleetService.MoveMemberAsync(b.Id, FleetRole.SquadMember, wingId, squadId, Owner);

        var (ok, _) = await client.SwapMembersAsync(a.Id, b.Id);

        Assert.True(ok);
        var after = await repository.ListMembersAsync(fleetId);
        var aAfter = after.First(m => m.Id == a.Id);
        var bAfter = after.First(m => m.Id == b.Id);
        Assert.Equal(FleetRole.SquadMember, aAfter.Role);
        Assert.Equal(squadId, aAfter.SquadId);
        Assert.Equal(FleetRole.WingCommander, bAfter.Role);
        Assert.Equal(-1, bAfter.SquadId);
    }
}
