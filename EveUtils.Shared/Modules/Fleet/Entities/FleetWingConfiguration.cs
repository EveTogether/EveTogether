using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>EF mapping for <see cref="FleetWing"/>. Cascade-deleted with its <see cref="Fleet"/>.</summary>
public sealed class FleetWingConfiguration : IEntityTypeConfiguration<FleetWing>
{
    public void Configure(EntityTypeBuilder<FleetWing> builder)
    {
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Name).IsRequired().HasMaxLength(255);
        builder.HasIndex(w => w.FleetId);

        builder.HasOne<Fleet>()
            .WithMany()
            .HasForeignKey(w => w.FleetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
