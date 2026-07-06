using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Ships.Entities;

/// <summary>EF mapping for the Ships module. Applied by the appropriate context.</summary>
public sealed class ShipConfiguration : IEntityTypeConfiguration<Ship>
{
    public void Configure(EntityTypeBuilder<Ship> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).HasMaxLength(255);
        builder.Property(s => s.Class).HasMaxLength(100);

        builder.HasMany(s => s.Fittings)
            .WithOne(f => f.Ship)
            .HasForeignKey(f => f.ShipId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
