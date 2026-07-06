using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>EF mapping for <see cref="FleetSquad"/>. Cascade-deleted with its <see cref="FleetWing"/>
/// (a single Fleet→Wing→Squad chain, so no multiple-cascade-path issue on SQL Server).</summary>
public sealed class FleetSquadConfiguration : IEntityTypeConfiguration<FleetSquad>
{
    public void Configure(EntityTypeBuilder<FleetSquad> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).IsRequired().HasMaxLength(255);
        builder.HasIndex(s => s.WingId);

        builder.HasOne<FleetWing>()
            .WithMany()
            .HasForeignKey(s => s.WingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
