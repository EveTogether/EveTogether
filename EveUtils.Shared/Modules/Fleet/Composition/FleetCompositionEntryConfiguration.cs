using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>EF mapping for <see cref="FleetCompositionEntry"/>. Cascade-deleted with its
/// <see cref="FleetCompositionRole"/>; the <see cref="FleetCompositionEntry.Fit"/> snapshot is an owned (required)
/// type table-split onto the entry row.</summary>
public sealed class FleetCompositionEntryConfiguration : IEntityTypeConfiguration<FleetCompositionEntry>
{
    public void Configure(EntityTypeBuilder<FleetCompositionEntry> builder)
    {
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.RoleId);

        builder.HasOne<FleetCompositionRole>()
            .WithMany()
            .HasForeignKey(e => e.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.OwnsOne(e => e.Fit, FitReferenceMapping.Configure);
        builder.Navigation(e => e.Fit).IsRequired();
    }
}
