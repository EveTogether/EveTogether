using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-126 — one counter-proof per acceptance criterion, all against <see cref="EscalationDialogViewModel"/> directly:
/// what ET-126 adds is the resolution against the catalogue, and ET-125's own tests already cover the storage
/// plumbing that carries the result to SAVE.
/// </summary>
public sealed class EscalationCatalogEnrichmentTests
{
    /// <summary>AC-1: a typed name with one unambiguous exact match yields the dungeonId, without the pilot ever
    /// picking it from <see cref="EscalationDialogViewModel.SiteResults"/>. Must be red against an implementation
    /// that stores only the typed name (ET-125's own <c>Register</c>, before this ticket's fallback).</summary>
    [Fact]
    public void UnambiguousTypedName_ResolvesTheDungeonId_WithoutASelection()
    {
        var sde = new FakeSdeAccessor()
            .AddSite(new SdeSite(90001, "Sansha Refuge", null, "Escalation", null, "Sansha's Nation", null, null, false, []));
        var dialog = new EscalationDialogViewModel(sde) { SiteQuery = "Sansha Refuge", RemainingTimeText = "5:00:00" };

        dialog.RegisterCommand.Execute(null);

        Assert.NotNull(dialog.Result);
        Assert.Equal(90001, dialog.Result!.DungeonId);
    }

    /// <summary>AC-2 — carries the whole ticket's value. <c>Sansha's Command Relay Outpost</c> exists as <c>2251</c>
    /// (a Combat Site) and <c>2406</c> (an Escalation), both Sansha's Nation, both DED 3: the enrichment must show
    /// the faction and the DED rating and leave the archetype out. An implementation that takes the first match
    /// would show "Combat Site" here and fail.</summary>
    [Fact]
    public void AmbiguousName_ShowsWhatTheMatchesShare_AndOmitsTheArchetypeTheyDisagreeOn()
    {
        const string collidingName = "Sansha's Command Relay Outpost";
        var sde = new FakeSdeAccessor()
            .AddSite(new SdeSite(2251, collidingName, null, "Combat Site", null, "Sansha's Nation", null, 3, false, []))
            .AddSite(new SdeSite(2406, collidingName, null, "Escalation", null, "Sansha's Nation", null, 3, false, []));
        var dialog = new EscalationDialogViewModel(sde) { SiteQuery = collidingName };

        Assert.Contains("Sansha's Nation", dialog.CatalogEnrichmentText);
        Assert.Contains("DED 3", dialog.CatalogEnrichmentText);
        Assert.DoesNotContain("Combat Site", dialog.CatalogEnrichmentText);
        Assert.DoesNotContain("Escalation", dialog.CatalogEnrichmentText);
    }

    /// <summary>ET-451: Register carries an escalation site, never free text — a name the catalogue does not carry,
    /// or one naming only a site of another archetype, cannot be registered. Replaces ET-126 AC-3 (a typed name
    /// registered plainly), on Jithran's decision of 2026-10-05.</summary>
    [Theory]
    [InlineData("A Site Nobody Has Scanned Yet")]
    [InlineData("Angel Hideaway")]
    public void NameOfNoEscalationSite_CannotBeRegistered(string typed)
    {
        var sde = new FakeSdeAccessor()
            .AddSite(new SdeSite(3000, "Angel Hideaway", null, "Combat Site", null, "Angel Cartel", null, 2, false, []));
        var dialog = new EscalationDialogViewModel(sde) { SiteQuery = typed, RemainingTimeText = "1:00:00" };

        Assert.Empty(dialog.SiteResults);
        Assert.False(dialog.RegisterCommand.CanExecute(null));
    }

    /// <summary>ET-451: before anything is typed the picker already lists every escalation site and nothing else —
    /// what this store registered before from the same source site first, then the source site's own faction, then
    /// the rest by name. The SDE carries no source→escalation mapping, so these two are all there is to rank by.</summary>
    [Fact]
    public void EmptyQuery_ListsOnlyEscalationSites_HistoryThenFactionFirst()
    {
        var refuge = new SdeSite(1000, "Sansha Refuge", null, "Combat Sites", 500019, "Sansha's Nation", null, 2, false, []);
        var sde = new FakeSdeAccessor()
            .AddSite(refuge)
            .AddSite(new SdeSite(2001, "Angel Naval Shipyard", null, "Escalation", 500011, "Angel Cartel", null, 4, false, []))
            .AddSite(new SdeSite(2002, "Sansha War Supply Complex", null, "Escalation", 500019, "Sansha's Nation", null, 3, false, []))
            .AddSite(new SdeSite(2003, "Blood Raider Shipyard", null, "Escalation", 500012, "Blood Raider Covenant", null, 4, false, []));
        var dialog = new EscalationDialogViewModel(sde, sourceSites: [refuge], previouslyRegistered: [2003]);

        Assert.Equal([2003, 2002, 2001], dialog.SiteResults.Select(option => option.Site.DungeonId));
    }
}
