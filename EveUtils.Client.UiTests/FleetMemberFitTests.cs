using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Per-member fit assignment: assigning through the fleet client persists the fit snapshot on the member
/// and surfaces it in the member info and roster node; the roster's assign action drives the single fit picker and
/// reloads; a null fit clears it.
/// </summary>
public class FleetMemberFitTests
{
    private const int Owner = 95000001;

    private static async Task SeedCharacterAsync(IServiceProvider services, int characterId, string name) =>
        await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, characterId));

    private static FleetInfo InfoFor(EveUtils.Shared.Modules.Fleet.Entities.Fleet fleet) =>
        new(fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation);

    private static async Task WaitForTreeAsync(FleetRosterViewModel roster)
    {
        for (var i = 0; i < 100 && roster.Tree.Count == 0; i++)
            await Task.Delay(50);
    }

    [AvaloniaFact]
    public async Task AssignMemberFit_Persists_MapsBack_AndClears()
    {
        using var instance = TestClientInstance.Create();
        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();

        var created = await service.CreateLocalFleetAsync("Fit test", null, Owner);
        var client = new LocalFleetClient(service, repository, characters, Owner);
        var member = (await client.ListMembersAsync(created.Value)).Single();   // the FC owner
        Assert.Null(member.AssignedFit);

        var fit = new FitReferenceInfo(11987, "Guardian — Armor", "{}", "h-guardian", null, null);
        Assert.True((await client.AssignMemberFitAsync(member.Id, fit, null)).Ok);

        var assigned = (await client.ListMembersAsync(created.Value)).Single().AssignedFit;
        Assert.NotNull(assigned);
        Assert.Equal("Guardian — Armor", assigned!.FitName);
        Assert.Equal(11987, assigned.ShipTypeId);

        Assert.True((await client.AssignMemberFitAsync(member.Id, null, null)).Ok);
        Assert.Null((await client.ListMembersAsync(created.Value)).Single().AssignedFit);
    }

    /// <summary>master-plan §5 (stream B / B-2): a member may set their OWN fit without owning the fleet (every pilot
    /// picks their ship), while another non-owner still cannot set someone else's fit. Red-without-fix: before the
    /// owner-OR-self guard, the member's own assign was rejected by the creator-only structure guard.</summary>
    [AvaloniaFact]
    public async Task AssignMemberFit_ByTheMemberThemselves_IsAllowed_ByAnotherNonOwner_IsDenied()
    {
        const int memberChar = 95000002;
        const int outsider = 95000003;
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Owner");
        await SeedCharacterAsync(instance.Services, memberChar, "Member");
        await SeedCharacterAsync(instance.Services, outsider, "Outsider");

        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();
        var created = await service.CreateLocalFleetAsync("Self-assign test", null, Owner);

        // The owner adds the member's character to the fleet.
        Assert.True((await service.AddLocalCharacterAsync(created.Value, memberChar, Owner)).IsSuccess);

        var ownerClient = new LocalFleetClient(service, repository, characters, Owner);
        var memberRow = (await ownerClient.ListMembersAsync(created.Value)).Single(m => m.CharacterId == memberChar);

        var fit = new FitReferenceInfo(11987, "Guardian — Armor", "{}", "h-guardian", null, null);

        // The member sets their OWN fit → allowed (master-plan §5), even though they are not the creator.
        var memberClient = new LocalFleetClient(service, repository, characters, memberChar);
        Assert.True((await memberClient.AssignMemberFitAsync(memberRow.Id, fit, null)).Ok);

        // A different non-owner character cannot set this member's fit → denied.
        var outsiderClient = new LocalFleetClient(service, repository, characters, outsider);
        Assert.False((await outsiderClient.AssignMemberFitAsync(memberRow.Id, fit, null)).Ok);
    }

    [AvaloniaFact]
    public async Task Roster_AssignFit_PicksThroughTheSinglePicker_AndShowsIt()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();
        var created = await service.CreateLocalFleetAsync("Assign test", null, Owner);
        var client = new LocalFleetClient(service, repository, characters, Owner);
        var roster = new FleetRosterViewModel(instance.Services, client, InfoFor((await repository.GetAsync(created.Value))!), isOwner: true, Owner);
        await WaitForTreeAsync(roster);

        var fit = new FitReferenceInfo(11987, "Guardian — Armor", "{}", "h-guardian", null, null);
        recording.OnPickFit = _ => Task.FromResult<FitReferenceInfo?>(fit);

        var commander = ((FleetRootNodeViewModel)roster.Tree[0]).Commander;
        Assert.False(commander!.HasAssignedFit);

        await commander.AssignFitCommand.ExecuteAsync(null);

        var refreshed = ((FleetRootNodeViewModel)roster.Tree[0]).Commander!;
        Assert.True(refreshed.HasAssignedFit);
        Assert.Equal("Guardian — Armor", refreshed.Member.AssignedFit!.FitName);
        Assert.Equal("CHANGE FIT", refreshed.AssignFitButtonLabel);
    }

    [AvaloniaFact]
    public async Task Roster_OpenMemberFit_ShowsFitDetail()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();
        var created = await service.CreateLocalFleetAsync("Open test", null, Owner);
        var client = new LocalFleetClient(service, repository, characters, Owner);

        var fc = (await client.ListMembersAsync(created.Value)).Single();
        var rawJson = JsonSerializer.Serialize(new EsiFitting(0, "Guardian — Armor", "", 11987, new List<EsiFittingItem>()));
        await client.AssignMemberFitAsync(fc.Id, new FitReferenceInfo(11987, "Guardian — Armor", rawJson, "h-guardian", null, null), null);

        var roster = new FleetRosterViewModel(instance.Services, client, InfoFor((await repository.GetAsync(created.Value))!), isOwner: true, Owner);
        await WaitForTreeAsync(roster);

        await ((FleetRootNodeViewModel)roster.Tree[0]).Commander!.OpenFitCommand.ExecuteAsync(null);

        Assert.NotNull(recording.LastFitDetail);
        Assert.Equal("Guardian — Armor", recording.LastFitDetail!.Name);
    }

    [AvaloniaFact]
    public async Task FleetRosterWindow_WithAssignedFit_Renders()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();
        var created = await service.CreateLocalFleetAsync("Render test", null, Owner);
        var client = new LocalFleetClient(service, repository, characters, Owner);

        var fc = (await client.ListMembersAsync(created.Value)).Single();
        await client.AssignMemberFitAsync(fc.Id, new FitReferenceInfo(11987, "Guardian — Armor", "{}", "h-guardian", null, null), null);
        await service.AddExternalAsync(created.Value, 96000001, Owner);   // a leaf member (unassigned → "PICK FIT")

        var roster = new FleetRosterViewModel(instance.Services, client, InfoFor((await repository.GetAsync(created.Value))!), isOwner: true, Owner);
        await WaitForTreeAsync(roster);

        var window = new FleetRosterWindow(roster) { Width = 760, Height = 560 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-roster-assigned-fit.png");
        window.Close();
    }
}
