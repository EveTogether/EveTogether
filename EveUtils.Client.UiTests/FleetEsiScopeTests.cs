using System.Linq;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fleet;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// of the ESI fleet-coupling: the fleet scopes are declared opt-in (Q1 — read access is per character, not
/// default-on). Guards both the catalog declaration and the consent-dialog default: a fresh sign-in leaves opt-in
/// scopes unticked, while a re-auth still pre-ticks exactly what was already granted.
/// </summary>
public class FleetEsiScopeTests
{
    [Fact]
    public void FleetsScopeCatalog_DeclaresReadAndWrite_AsOptInClientScopes()
    {
        var reqs = FleetsScopeCatalog.Catalog.Requirements;

        var read = Assert.Single(reqs, r => r.Scope == FleetsScopeCatalog.ReadFleet);
        var write = Assert.Single(reqs, r => r.Scope == FleetsScopeCatalog.WriteFleet);
        Assert.Equal("esi-fleets.read_fleet.v1", read.Scope);
        Assert.Equal("esi-fleets.write_fleet.v1", write.Scope);
        Assert.All(new[] { read, write }, r =>
        {
            Assert.True(r.OptIn, "fleet scopes must be opt-in (Q1)");
            Assert.Equal(EsiScopeTarget.Client, r.Target);
        });
    }

    [AvaloniaFact]
    public void FreshSignIn_LeavesOptInFleetScopeUnticked_ButPreSelectsNormalScopes()
    {
        var available = new[]
        {
            new EsiScopeRequirement("esi-skills.read_skills.v1", EsiScopeTarget.Client, "Skills"),
            new EsiScopeRequirement(FleetsScopeCatalog.ReadFleet, EsiScopeTarget.Client, "Fleet", OptIn: true),
        };

        var window = new ScopeSelectionWindow(available, preselected: null); // fresh sign-in

        Assert.True(window.Choices.Single(c => c.Scope == "esi-skills.read_skills.v1").IsSelected);
        Assert.False(window.Choices.Single(c => c.Scope == FleetsScopeCatalog.ReadFleet).IsSelected); // Q1: opt-in
    }

    [AvaloniaFact]
    public void ReAuth_PreTicksOnlyGrantedScopes_EvenWhenOptIn()
    {
        var available = new[]
        {
            new EsiScopeRequirement("esi-skills.read_skills.v1", EsiScopeTarget.Client, "Skills"),
            new EsiScopeRequirement(FleetsScopeCatalog.ReadFleet, EsiScopeTarget.Client, "Fleet", OptIn: true),
        };

        var window = new ScopeSelectionWindow(available, preselected: new[] { FleetsScopeCatalog.ReadFleet });

        Assert.False(window.Choices.Single(c => c.Scope == "esi-skills.read_skills.v1").IsSelected); // not granted
        Assert.True(window.Choices.Single(c => c.Scope == FleetsScopeCatalog.ReadFleet).IsSelected);  // granted → ticked
    }
}
