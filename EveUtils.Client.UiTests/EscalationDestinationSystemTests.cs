using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-127 — one counter-proof per acceptance criterion, both against <see cref="EscalationDialogViewModel"/>
/// directly: this type only ever carries an <see cref="EveUtils.Shared.Modules.Sde.ISdeAccessor"/>, so there is no
/// ESI to reach for even by accident. AC-3 (the jump count carrying its own anchor) lives in
/// <see cref="EscalationJumpDistanceTests"/> instead, since that is the one thing this ticket actually asks ESI for.
/// </summary>
public sealed class EscalationDestinationSystemTests
{
    /// <summary>AC-1: the ticket's whole saving — a destination typed as the Agency showed it resolves to the
    /// catalogue id and its security straight off the SDE, with no ESI layer in reach to fall back on. Must be red
    /// against an implementation that resolves this through <c>POST /universe/ids</c> or
    /// <c>GET /universe/systems/{id}</c> instead.</summary>
    [Fact]
    public void TypedDestination_ResolvesIdAndSecurity_FromTheSdeAlone()
    {
        var sde = new FakeSdeAccessor().AddSolarSystem(new SdeSolarSystem(30003867, "Ervekam", 0.69));
        var dialog = new EscalationDialogViewModel(sde)
        {
            SiteQuery = "Sansha Refuge", DestinationSystem = "Ervekam", RemainingTimeText = "1:00:00"
        };

        Assert.Equal(30003867, dialog.DestinationResolvedSystem?.SolarSystemId);
        Assert.Equal("0.7 security", dialog.DestinationSecurityText);

        dialog.RegisterCommand.Execute(null);

        Assert.NotNull(dialog.Result);
        Assert.Equal(30003867, dialog.Result!.DestinationSolarSystemId);
    }

    /// <summary>AC-2: a destination the catalogue has never seen is not an error — no enrichment, and the
    /// escalation still registers on the typed name alone, with no solarSystemId to show for it. Same rule as
    /// ET-126 AC-3.</summary>
    [Fact]
    public void UnknownDestination_RegistersPlainly_WithNoEnrichmentAndNoError()
    {
        var dialog = new EscalationDialogViewModel(new FakeSdeAccessor())
        {
            SiteQuery = "Sansha Refuge", DestinationSystem = "Nowhereton", RemainingTimeText = "1:00:00"
        };

        Assert.Null(dialog.DestinationResolvedSystem);
        Assert.Null(dialog.DestinationSecurityText);

        dialog.RegisterCommand.Execute(null);

        Assert.NotNull(dialog.Result);
        Assert.Equal("Nowhereton", dialog.Result!.DestinationSystem);
        Assert.Null(dialog.Result.DestinationSolarSystemId);
    }
}
