using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// member self-report: a pilot's own client confirms its in-game fleet presence so the roster reflects who
/// has actually joined even when we are not the boss (the boss-only roster read can't see it). Presence is stored on
/// the member's <c>EsiMemberId</c> (= character id). Covers the handler's self-only + idempotence rules
/// and the confirm/retract round-trip.
/// </summary>
public class ReportMemberInGameFleetTests
{
    private const int Owner = 95000040;
    private const int Other = 96000040;

    [AvaloniaFact]
    public async Task ReportInGameFleet_IsSelfOnly_StoresPresence_AndRetracts()
    {
        using var instance = TestClientInstance.Create();
        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();

        var fleetId = (await service.CreateLocalFleetAsync("self-report test", null, Owner)).Value;
        var client = new LocalFleetClient(service, repository, characters, Owner);
        var fc = (await client.ListMembersAsync(fleetId)).Single();

        // Someone else may NOT report the pilot's in-game presence.
        var foreign = await service.ReportMemberInGameFleetAsync(fc.Id, inFleet: true, Other);
        Assert.False(foreign.IsSuccess);

        // The pilot's own client may — presence lands on the member's EsiMemberId (= character id).
        var own = await service.ReportMemberInGameFleetAsync(fc.Id, inFleet: true, Owner);
        Assert.True(own.IsSuccess);
        Assert.True(own.Value); // first report = a change
        Assert.Equal((long)Owner, (await repository.GetMemberAsync(fc.Id))!.EsiMemberId!.Value);

        // Idempotent: re-reporting the same presence is no change → the caller skips the broadcast.
        var again = await service.ReportMemberInGameFleetAsync(fc.Id, inFleet: true, Owner);
        Assert.True(again.IsSuccess);
        Assert.False(again.Value);

        // Retracting (left the in-game fleet) clears it and counts as a change.
        var left = await service.ReportMemberInGameFleetAsync(fc.Id, inFleet: false, Owner);
        Assert.True(left.IsSuccess);
        Assert.True(left.Value);
        Assert.Null((await repository.GetMemberAsync(fc.Id))!.EsiMemberId);
    }
}
