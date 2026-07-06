using System;
using System.Linq;
using System.Threading.Tasks;
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
/// Regression: a client-only (local) fleet that multi-boxes several of my characters must feed EVERY member's live
/// graph, not only the acting/creator one. The bug had <see cref="FleetsViewModel.UpdateParticipation"/> register
/// just the acting character of a local fleet, so the fleet-metrics window showed data for the FC alone and every
/// alt flatlined (no DPS/cap/bounty/location). Server fleets already fanned out over their members.
/// </summary>
public class LocalFleetParticipationTests
{
    [AvaloniaFact]
    public async Task LocalFleet_RegistersEveryMemberCharacter_NotOnlyTheActingOne()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var registry = services.GetRequiredService<ICharacterRegistry>();
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var participation = services.GetRequiredService<IFleetParticipation>();

        const int fc = 95000001, alt1 = 95000002, alt2 = 95000003;
        await registry.AddOrUpdateAsync(new Character("Jithran", fc));
        await registry.AddOrUpdateAsync(new Character("Abnoba Auscent", alt1));
        await registry.AddOrUpdateAsync(new Character("ColdSprockets", alt2));

        var created = await fleetService.CreateLocalFleetAsync("HF", null, fc);
        Assert.True(created.IsSuccess);
        var fleetId = created.Value;

        var now = DateTimeOffset.UtcNow;
        await repository.AddMemberAsync(new FleetMember
            { FleetId = fleetId, CharacterId = alt1, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1, JoinTime = now });
        await repository.AddMemberAsync(new FleetMember
            { FleetId = fleetId, CharacterId = alt2, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1, JoinTime = now });

        var vm = new FleetsViewModel(services);
        for (var i = 0; i < 100 && vm.LocalFleets.Count == 0; i++)
            await Task.Delay(50);

        Assert.Single(vm.LocalFleets);
        var participants = participation.Current.Where(p => p.FleetId == fleetId).ToList();
        var ids = participants.Select(p => p.CharacterId).ToHashSet();
        Assert.Contains(fc, ids);
        Assert.Contains(alt1, ids);
        Assert.Contains(alt2, ids);
        Assert.All(participants, p => Assert.True(p.ClientOnly)); // local fleets feed local graphs only
    }
}
