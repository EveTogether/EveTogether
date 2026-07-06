using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>
/// Shared owned-type mapping for <see cref="FitReference"/> so the one snapshot shape is configured identically
/// wherever it is embedded (a composition entry's required fit and a member's optional assigned fit) — one
/// definition, no per-owner duplication. The columns are table-split onto the owner.
/// <see cref="FitReference.FitName"/>/<see cref="FitReference.ContentHash"/> are marked required so EF has an
/// identifying property for the optional-owner case (member) and can tell an absent fit from a present one.
/// </summary>
internal static class FitReferenceMapping
{
    public static void Configure<TOwner>(OwnedNavigationBuilder<TOwner, FitReference> fit) where TOwner : class
    {
        fit.Property(f => f.FitName).IsRequired().HasMaxLength(255);
        fit.Property(f => f.ContentHash).IsRequired().HasMaxLength(128);
        fit.Property(f => f.RawJson).IsRequired(); // verbatim ESI JSON; no length cap (TEXT).
    }
}
