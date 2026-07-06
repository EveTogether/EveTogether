using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The coupled-composition two-level fill overview: the roster shows, per role-group, how many members
/// fly a fit that fills the group minimum ("DPS 1/40") and the per-fit minima where set ("Guardian 2/3 · Scimitar
/// 1/2"). The join is each member's recorded composition-entry tag (<see cref="FleetMemberInfo.AssignedCompositionEntryId"/>),
/// so a member flying an own fit outside the doctrine does not count. Drives the real <see cref="FleetRosterViewModel"/>
/// over a client-only fleet with a real coupled composition.
/// </summary>
public class FleetRosterCompositionFillTests
{
    private const int Owner = 95000020;

    private static FleetInfo InfoFor(EveUtils.Shared.Modules.Fleet.Entities.Fleet fleet) =>
        new(fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation);

    private static FitReferenceInfo Fit(int shipTypeId, string name, string hash) =>
        new(shipTypeId, name, "{}", hash, null, null);

    // A coupled client-only fleet: four members, a doctrine (DPS ≥40; Logistics ≥5 with Guardian ≥3 + Scimitar ≥2) and
    // one Ferox / two Guardian / one Scimitar assigned. Carries the entry ids so a test can edit the live doctrine.
    private sealed record CoupledScenario(
        LocalFleetClient Client, LocalFleetCompositionClient Compositions,
        long FleetId, long CompositionId, long FeroxEntryId, long GuardianEntryId, long ScimitarEntryId);

    private static async Task<CoupledScenario> BuildCoupledScenarioAsync(IServiceProvider services)
    {
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();
        var compositionRepository = services.GetRequiredService<IFleetCompositionRepository>();

        var client = new LocalFleetClient(fleetService, repository, characters, Owner);
        var compositions = new LocalFleetCompositionClient(fleetService, compositionRepository, Owner);

        // A fleet with the owner (FC) plus three external members → four members to assign.
        var created = await fleetService.CreateLocalFleetAsync("fill test", null, Owner);
        var fleetId = created.Value;
        foreach (var id in new[] { 96000001, 96000002, 96000003 })
            Assert.True((await fleetService.AddExternalAsync(fleetId, id, Owner)).IsSuccess);

        // Doctrine: DPS (≥40, no per-fit min) + Logistics (≥5, Guardian ≥3, Scimitar ≥2).
        var composition = await compositions.CreateAsync("Homefront Vanguard", null);
        var dpsRole = await compositions.AddRoleAsync(composition.Id, "DPS", 40);
        await compositions.AddEntryAsync(dpsRole.Id, Fit(16227, "Ferox — Blaster", "h-ferox"), null);
        var logiRole = await compositions.AddRoleAsync(composition.Id, "Logistics", 5);
        var guardian = await compositions.AddEntryAsync(logiRole.Id, Fit(11987, "Guardian — Armor", "h-guardian"), 3);
        var scimitar = await compositions.AddEntryAsync(logiRole.Id, Fit(11978, "Scimitar — Shield", "h-scimitar"), 2);
        var ferox = (await compositions.GetAsync(composition.Id))!.Roles.Single(r => r.RoleName == "DPS").Entries[0];

        Assert.True((await client.SetFleetCompositionAsync(fleetId, composition.Id)).Ok);

        // Tag the four members against doctrine entries: 1 DPS (Ferox), 2 Guardian, 1 Scimitar.
        var members = await client.ListMembersAsync(fleetId);
        var assignments = new (long EntryId, FitReferenceInfo Fit)[]
        {
            (ferox.Id, Fit(16227, "Ferox — Blaster", "h-ferox")),
            (guardian.Id, Fit(11987, "Guardian — Armor", "h-guardian")),
            (guardian.Id, Fit(11987, "Guardian — Armor", "h-guardian")),
            (scimitar.Id, Fit(11978, "Scimitar — Shield", "h-scimitar")),
        };
        for (var i = 0; i < members.Count; i++)
            Assert.True((await client.AssignMemberFitAsync(members[i].Id, assignments[i].Fit, assignments[i].EntryId)).Ok);

        return new CoupledScenario(client, compositions, fleetId, composition.Id, ferox.Id, guardian.Id, scimitar.Id);
    }

    private static async Task<FleetRosterViewModel> RosterFor(IServiceProvider services, CoupledScenario scenario)
    {
        var fleet = await services.GetRequiredService<IFleetRepository>().GetAsync(scenario.FleetId);
        var roster = new FleetRosterViewModel(
            services, scenario.Client, InfoFor(fleet!), isOwner: true, Owner, compositions: scenario.Compositions);
        for (var i = 0; i < 100 && !roster.HasCompositionFill; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        return roster;
    }

    /// <summary>Builds a client-only fleet with four members, a coupled doctrine (DPS ≥40; Logistics ≥5 with Guardian
    /// ≥3 + Scimitar ≥2) and one Ferox / two Guardian / one Scimitar assigned, then returns its loaded roster VM.</summary>
    private static async Task<FleetRosterViewModel> BuildFilledRosterAsync(IServiceProvider services) =>
        await RosterFor(services, await BuildCoupledScenarioAsync(services));

    [AvaloniaFact]
    public async Task Roster_ShowsTwoLevelFill_FromCoupledCompositionAndAssignedEntries()
    {
        using var instance = TestClientInstance.Create();
        using var roster = await BuildFilledRosterAsync(instance.Services);

        Assert.True(roster.HasCompositionFill);

        var dps = roster.CompositionFill.Single(r => r.RoleName == "DPS");
        Assert.Equal("1 / 40", dps.GroupCount);   // one Ferox assigned
        Assert.False(dps.HasEntries);             // no per-fit minimum on the DPS role

        var logi = roster.CompositionFill.Single(r => r.RoleName == "Logistics");
        Assert.Equal("3 / 5", logi.GroupCount);   // 2 Guardian + 1 Scimitar fill the group
        Assert.Equal("Guardian — Armor", logi.Entries[0].FitName);
        Assert.Equal("2 / 3", logi.Entries[0].Count);
        Assert.Equal("Scimitar — Shield", logi.Entries[1].FitName);
        Assert.Equal("1 / 2", logi.Entries[1].Count);
    }

    [AvaloniaFact]
    public async Task FleetRosterWindow_WithTwoLevelFill_Renders()
    {
        using var instance = TestClientInstance.Create();
        using var roster = await BuildFilledRosterAsync(instance.Services);

        var window = new FleetRosterWindow(roster) { Width = 760, Height = 560 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-roster-composition-fill.png");
        window.Close();
    }

    // ── Editing a coupled composition (plan §7): entries are snapshotted at assignment, the composition itself
    // stays freely editable. So later doctrine edits never touch the fits members already fly, and the live fill just
    // reflects the current doctrine (an entry the FC removed simply stops being tracked). ────────────────────────────

    [AvaloniaFact]
    public async Task EditingCoupledDoctrine_LeavesAlreadyAssignedMemberFits_Untouched()
    {
        using var instance = TestClientInstance.Create();
        var scenario = await BuildCoupledScenarioAsync(instance.Services);

        // Edit the doctrine freely while it is coupled: drop the Guardian entry and bump Scimitar's per-fit minimum.
        Assert.True((await scenario.Compositions.RemoveEntryAsync(scenario.GuardianEntryId)).Ok);
        Assert.True((await scenario.Compositions.EditEntryAsync(scenario.ScimitarEntryId, 9)).Ok);

        // Both Guardian members still fly the exact fit snapshot they were assigned — the edit didn't reach into the
        // roster. Their composition-entry tag is kept as provenance (now dangling), not rewritten or cleared.
        var members = await scenario.Client.ListMembersAsync(scenario.FleetId);
        var guardians = members.Where(m => m.AssignedFit?.ContentHash == "h-guardian").ToList();
        Assert.Equal(2, guardians.Count);
        Assert.All(guardians, m => Assert.Equal("Guardian — Armor", m.AssignedFit!.FitName));
        Assert.All(guardians, m => Assert.Equal(scenario.GuardianEntryId, m.AssignedCompositionEntryId));
    }

    [AvaloniaFact]
    public async Task RemovingEntryFromCoupledDoctrine_DropsItFromTheLiveFill_WithoutLosingMemberFits()
    {
        using var instance = TestClientInstance.Create();
        var scenario = await BuildCoupledScenarioAsync(instance.Services);

        // The FC removes Guardian from the doctrine while the fleet is coupled (composition stays freely editable).
        Assert.True((await scenario.Compositions.RemoveEntryAsync(scenario.GuardianEntryId)).Ok);

        using var roster = await RosterFor(instance.Services, scenario);

        var logi = roster.CompositionFill.Single(r => r.RoleName == "Logistics");
        Assert.DoesNotContain(logi.Entries, e => e.FitName == "Guardian — Armor");   // the removed entry is gone from the live fill
        Assert.Equal("1 / 5", logi.GroupCount);   // only the Scimitar still maps to a current entry; the two Guardians orphan out

        // …but the orphaned members keep their snapshot Guardian fit — the doctrine edit didn't strip the roster.
        var members = await scenario.Client.ListMembersAsync(scenario.FleetId);
        Assert.Equal(2, members.Count(m => m.AssignedFit?.ContentHash == "h-guardian"));
    }
}
