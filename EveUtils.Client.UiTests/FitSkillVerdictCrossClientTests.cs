using System.Collections.Generic;
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
/// cross-client can-fly verdicts: trained skills never leave the pilot's client, so that client reports
/// only its verdict (<see cref="FitSkillVerdict"/>) and viewers without local skill data show the can-fly / warning badge from the
/// wire value. Covers the handler's self-only + idempotence rules, the assign-resets-verdict invalidation, the badge
/// fallback on the roster node, and the roster window's self-report.
/// </summary>
public class FitSkillVerdictCrossClientTests
{
    private const int Owner = 95000040;
    private const int Other = 96000040;

    private static FitReferenceInfo Fit() => new(11987, "Guardian — Armor", "{}", "h-guardian", null, null);

    private static FleetInfo InfoFor(EveUtils.Shared.Modules.Fleet.Entities.Fleet fleet) =>
        new(fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation);

    private static async Task WaitForTreeAsync(FleetRosterViewModel roster)
    {
        for (var i = 0; i < 100 && roster.Tree.Count == 0; i++) await Task.Delay(50);
    }

    [AvaloniaFact]
    public async Task Report_IsSelfOnly_AndStoresTheVerdict()
    {
        using var instance = TestClientInstance.Create();
        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();

        var fleetId = (await service.CreateLocalFleetAsync("verdict test", null, Owner)).Value;
        var client = new LocalFleetClient(service, repository, characters, Owner);
        var fc = (await client.ListMembersAsync(fleetId)).Single();
        Assert.True((await client.AssignMemberFitAsync(fc.Id, Fit(), null)).Ok);

        // Someone else (the owner acting for another pilot included) may NOT speak for the pilot's skills.
        var foreign = await service.ReportMemberFitVerdictAsync(fc.Id, FitSkillVerdict.CanFly, Other);
        Assert.False(foreign.IsSuccess);

        // The pilot's own client may — and the verdict lands on the wire-visible member row.
        var own = await service.ReportMemberFitVerdictAsync(fc.Id, FitSkillVerdict.CanFly, Owner);
        Assert.True(own.IsSuccess);
        Assert.True(own.Value); // first report = a change
        Assert.Equal(FitSkillVerdict.CanFly, (await client.ListMembersAsync(fleetId)).Single().FitSkillVerdict);

        // Idempotent: re-reporting the same verdict is no change → the caller skips the broadcast.
        var again = await service.ReportMemberFitVerdictAsync(fc.Id, FitSkillVerdict.CanFly, Owner);
        Assert.True(again.IsSuccess);
        Assert.False(again.Value);
    }

    [AvaloniaFact]
    public async Task AssigningAnotherFit_ResetsTheReportedVerdict()
    {
        using var instance = TestClientInstance.Create();
        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();

        var fleetId = (await service.CreateLocalFleetAsync("verdict reset", null, Owner)).Value;
        var client = new LocalFleetClient(service, repository, characters, Owner);
        var fc = (await client.ListMembersAsync(fleetId)).Single();
        await client.AssignMemberFitAsync(fc.Id, Fit(), null);
        await service.ReportMemberFitVerdictAsync(fc.Id, FitSkillVerdict.MissingSkills, Owner);
        Assert.Equal(FitSkillVerdict.MissingSkills, (await client.ListMembersAsync(fleetId)).Single().FitSkillVerdict);

        // A different fit invalidates the old verdict — it was about the previous fit.
        await client.AssignMemberFitAsync(fc.Id, new FitReferenceInfo(641, "Megathron — Rails", "{}", "h-mega", null, null), null);
        Assert.Equal(FitSkillVerdict.Unknown, (await client.ListMembersAsync(fleetId)).Single().FitSkillVerdict);
    }

    [AvaloniaFact]
    public async Task RosterBadge_FallsBackToWireVerdict_WhenSkillsAreNotLocallyKnown()
    {
        // A remote pilot's member row carries CanFly from their own client; this client knows no skills for them
        // (the real evaluator yields no verdict), so the can-fly badge must come from the wire value.
        using var instance = TestClientInstance.Create();
        var fake = new FakeFleetClient
        {
            Members =
            [
                new FleetMemberInfo(1, Other, -1, -1, FleetRole.FleetCommander, false, Fit(), null, FitSkillVerdict.CanFly)
            ],
            Fleet = new FleetInfo(7, "remote", null, FleetVisibility.Public, FleetState.Active, Other,
                null, null, System.DateTimeOffset.UtcNow, FleetActivation.Forming)
        };

        using var roster = new FleetRosterViewModel(instance.Services, fake, fake.Fleet!, isOwner: false, Owner);
        await WaitForTreeAsync(roster);

        var commander = ((FleetRootNodeViewModel)roster.Tree[0]).Commander!;
        Assert.True(commander.CanFly);
        Assert.False(commander.HasSkillGap);
        Assert.Contains("reported by the pilot's client", commander.SkillBadgeTooltip);
    }

    [AvaloniaFact]
    public async Task Roster_ReportsOwnVerdict_OnlyWhenItDiffersFromTheWire()
    {
        // The acting pilot's client evaluates locally (stub: can fly) while the wire still says Unknown → report.
        using var instance = TestClientInstance.Create(s =>
            s.AddSingleton<IMemberFitSkillEvaluator>(new StubEvaluator(canFly: true)));
        var fake = new FakeFleetClient
        {
            Members = [new FleetMemberInfo(5, Owner, -1, -1, FleetRole.FleetCommander, false, Fit())],
            Fleet = new FleetInfo(8, "mine", null, FleetVisibility.Public, FleetState.Active, Owner,
                null, null, System.DateTimeOffset.UtcNow, FleetActivation.Forming)
        };

        using (var roster = new FleetRosterViewModel(instance.Services, fake, fake.Fleet!, isOwner: true, Owner))
        {
            await WaitForTreeAsync(roster);
            Assert.Equal((5L, FitSkillVerdict.CanFly), Assert.Single(fake.ReportedVerdicts));
        }

        // Wire already agrees → a reload must NOT re-report (this is what stops the report → reload loop).
        fake.ReportedVerdicts.Clear();
        fake.Members = [new FleetMemberInfo(5, Owner, -1, -1, FleetRole.FleetCommander, false, Fit(), null, FitSkillVerdict.CanFly)];
        using (var roster = new FleetRosterViewModel(instance.Services, fake, fake.Fleet!, isOwner: true, Owner))
        {
            await WaitForTreeAsync(roster);
            Assert.Empty(fake.ReportedVerdicts);
        }
    }

    /// <summary>Fixed-verdict evaluator standing in for "this client knows the acting pilot's skills".</summary>
    private sealed class StubEvaluator(bool canFly) : IMemberFitSkillEvaluator
    {
        public Task<MemberSkillBadge?> EvaluateAsync(int characterId, FitReferenceInfo? assignedFit) =>
            Task.FromResult<MemberSkillBadge?>(assignedFit is null
                ? null
                : new MemberSkillBadge(canFly, canFly ? "Can fly this fit" : "1 skill missing"));
    }
}
