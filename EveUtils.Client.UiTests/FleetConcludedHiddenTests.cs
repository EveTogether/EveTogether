using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Regression for "concluded fleets are hidden everywhere" (2026-06-10): once a fleet is Concluded it must drop out
/// of the participant-scoped lists (MyFleets/Participating) for both its creator and its members, and stay out of
/// discovery. Without the <see cref="FleetActivation.Concluded"/> filter in
/// <see cref="IFleetRepository.ListForParticipantAsync"/> the creator/member assertions are red. The row stays in
/// the DB (kept for history, not archived) — only the listings hide it.
/// </summary>
public class FleetConcludedHiddenTests
{
    private const int Owner = 95000042;
    private const int Member = 95000077;

    [AvaloniaFact]
    public async Task ConcludedFleet_DropsOutOf_ParticipantLists_ForOwnerAndMember()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();

        var created = await fleetService.CreateLocalFleetAsync("Conclude-hide test", null, Owner);
        Assert.True(created.IsSuccess);
        var fleetId = created.Value;

        await repository.AddMemberAsync(new FleetMember
        {
            FleetId = fleetId,
            CharacterId = Member,
            Role = FleetRole.SquadMember,
            IsExternal = false
        });

        // While not concluded, both the creator and the member see it in their participant list.
        Assert.Contains(await repository.ListForParticipantAsync(Owner), f => f.Id == fleetId);
        Assert.Contains(await repository.ListForParticipantAsync(Member), f => f.Id == fleetId);

        var client = new LocalFleetClient(fleetService, repository, characters, Owner);
        Assert.True((await client.StartFleetAsync(fleetId)).Ok);
        Assert.True((await client.ConcludeFleetAsync(fleetId)).Ok);

        // Concluded → hidden for the creator and the member, and never offered in discovery.
        Assert.DoesNotContain(await repository.ListForParticipantAsync(Owner), f => f.Id == fleetId);
        Assert.DoesNotContain(await repository.ListForParticipantAsync(Member), f => f.Id == fleetId);
        Assert.DoesNotContain(await repository.ListOpenAsync(), f => f.Id == fleetId);

        // It is kept for history, not archived — the listings hide it, the row survives.
        var after = await repository.GetAsync(fleetId);
        Assert.Equal(FleetActivation.Concluded, after!.Activation);
        Assert.Equal(FleetState.Active, after.State);
    }
}
