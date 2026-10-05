using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>EF mapping for <see cref="FleetCompositionEntry"/>. Cascade-deleted with its
/// <see cref="FleetCompositionRole"/>; the <see cref="FleetCompositionEntry.Fit"/> snapshot is an owned (required)
/// type table-split onto the entry row; its
/// <see cref="FleetCompositionEntry.SkillMinimums"/> are owned rows in their own table.</summary>
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

        // Owned rows in their own table, keyed on (EntryId, SkillTypeId): they load with the entry and the FK
        // cascades them away with it (and with its role or composition).
        builder.OwnsMany(e => e.SkillMinimums, minimum =>
        {
            minimum.ToTable("FleetCompositionEntrySkillMinimum");
            minimum.WithOwner().HasForeignKey("EntryId");
            minimum.HasKey("EntryId", nameof(FleetCompositionEntrySkillMinimum.SkillTypeId));
            minimum.Property(m => m.SkillTypeId).ValueGeneratedNever();
        });
    }
}
