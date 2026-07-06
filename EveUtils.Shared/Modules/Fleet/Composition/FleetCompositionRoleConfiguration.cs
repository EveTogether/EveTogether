using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>EF mapping for <see cref="FleetCompositionRole"/>. Cascade-deleted with its <see cref="FleetComposition"/>.</summary>
public sealed class FleetCompositionRoleConfiguration : IEntityTypeConfiguration<FleetCompositionRole>
{
    public void Configure(EntityTypeBuilder<FleetCompositionRole> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.RoleName).IsRequired().HasMaxLength(255);
        builder.HasIndex(r => r.CompositionId);

        builder.HasOne<FleetComposition>()
            .WithMany()
            .HasForeignKey(r => r.CompositionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
