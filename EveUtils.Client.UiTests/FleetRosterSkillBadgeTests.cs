using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The roster can-fly badge: each member with an assigned fit gets a check (can fly) or warning (missing
/// skills) verdict from <see cref="IMemberFitSkillEvaluator"/>, surfaced on the roster node. A stub evaluator drives the
/// verdicts so the wiring (pre-compute on reload → node passthrough) is tested without seeding a full SDE + skills.
/// </summary>
public class FleetRosterSkillBadgeTests
{
    private const int Owner = 95000030;
    private const int Member = 96000030;

    /// <summary>Verdict per character: can-fly for ids in <paramref name="canFly"/>, missing-skills for any other member with a fit.</summary>
    private sealed class StubEvaluator(HashSet<int> canFly) : IMemberFitSkillEvaluator
    {
        public Task<MemberSkillBadge?> EvaluateAsync(int characterId, FitReferenceInfo? assignedFit) =>
            Task.FromResult<MemberSkillBadge?>(assignedFit is null
                ? null
                : canFly.Contains(characterId)
                    ? new MemberSkillBadge(true, "Can fly this fit")
                    : new MemberSkillBadge(false, "2 skills missing"));
    }

    private static FleetInfo InfoFor(EveUtils.Shared.Modules.Fleet.Entities.Fleet fleet) =>
        new(fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation);

    private static FitReferenceInfo Fit() => new(11987, "Guardian — Armor", "{}", "h-guardian", null, null);

    private static async Task WaitForTreeAsync(FleetRosterViewModel roster)
    {
        for (var i = 0; i < 100 && roster.Tree.Count == 0; i++) await Task.Delay(50);
    }

    [AvaloniaFact]
    public async Task Roster_ShowsWarningBadge_WhenMemberLacksSkillsForAssignedFit()
    {
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IMemberFitSkillEvaluator>(new StubEvaluator([])));
        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();

        var created = await service.CreateLocalFleetAsync("badge test", null, Owner);
        var client = new LocalFleetClient(service, repository, characters, Owner);
        var fc = (await client.ListMembersAsync(created.Value)).Single();
        Assert.True((await client.AssignMemberFitAsync(fc.Id, Fit(), null)).Ok);

        using var roster = new FleetRosterViewModel(instance.Services, client, InfoFor((await repository.GetAsync(created.Value))!), isOwner: true, Owner);
        await WaitForTreeAsync(roster);

        var commander = ((FleetRootNodeViewModel)roster.Tree[0]).Commander!;
        Assert.True(commander.HasSkillGap);
        Assert.False(commander.CanFly);
        Assert.Equal("2 skills missing", commander.SkillBadgeTooltip);
        Assert.True(((FleetRootNodeViewModel)roster.Tree[0]).CommanderHasSkillGap);
    }

    [AvaloniaFact]
    public async Task Roster_ShowsCanFlyBadge_WhenMemberHasSkills()
    {
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IMemberFitSkillEvaluator>(new StubEvaluator([Owner])));
        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();

        var created = await service.CreateLocalFleetAsync("badge test", null, Owner);
        var client = new LocalFleetClient(service, repository, characters, Owner);
        var fc = (await client.ListMembersAsync(created.Value)).Single();
        Assert.True((await client.AssignMemberFitAsync(fc.Id, Fit(), null)).Ok);

        using var roster = new FleetRosterViewModel(instance.Services, client, InfoFor((await repository.GetAsync(created.Value))!), isOwner: true, Owner);
        await WaitForTreeAsync(roster);

        var commander = ((FleetRootNodeViewModel)roster.Tree[0]).Commander!;
        Assert.True(commander.CanFly);
        Assert.False(commander.HasSkillGap);
    }

    [AvaloniaFact]
    public async Task FleetRosterWindow_WithSkillBadges_Renders()
    {
        // Owner can-fly (commander header), an external leaf missing-skills — both badge states on screen at once.
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IMemberFitSkillEvaluator>(new StubEvaluator([Owner])));
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Pilot One", Owner));
        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();

        var created = await service.CreateLocalFleetAsync("badge render", null, Owner);
        var client = new LocalFleetClient(service, repository, characters, Owner);
        var fc = (await client.ListMembersAsync(created.Value)).Single();
        await client.AssignMemberFitAsync(fc.Id, Fit(), null);
        await service.AddExternalAsync(created.Value, Member, Owner);
        var leaf = (await client.ListMembersAsync(created.Value)).Single(m => m.CharacterId == Member);
        await client.AssignMemberFitAsync(leaf.Id, new FitReferenceInfo(11987, "Ferox — Blaster", "{}", "h-ferox", null, null), null);

        using var roster = new FleetRosterViewModel(instance.Services, client, InfoFor((await repository.GetAsync(created.Value))!), isOwner: true, Owner);
        await WaitForTreeAsync(roster);

        var window = new FleetRosterWindow(roster) { Width = 760, Height = 560 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-roster-skill-badges.png");
        window.Close();
    }
}
