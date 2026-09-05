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

    /// <summary>AC-3: a name the catalogue does not carry is not an error — no enrichment text, and the escalation
    /// still registers on the typed name alone, with no dungeonId to show for it.</summary>
    [Fact]
    public void NameNotInTheCatalogue_RegistersPlainly_WithNoEnrichmentAndNoError()
    {
        var dialog = new EscalationDialogViewModel(new FakeSdeAccessor())
        {
            SiteQuery = "A Site Nobody Has Scanned Yet", RemainingTimeText = "1:00:00"
        };

        Assert.Null(dialog.CatalogEnrichmentText);

        dialog.RegisterCommand.Execute(null);

        Assert.NotNull(dialog.Result);
        Assert.Equal("A Site Nobody Has Scanned Yet", dialog.Result!.SiteName);
        Assert.Null(dialog.Result.DungeonId);
    }
}
