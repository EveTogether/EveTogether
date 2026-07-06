using EveUtils.Shared.Modules.Fleet.Composition;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>EF mapping for <see cref="FleetMember"/>. Cascade-deleted with its <see cref="Fleet"/>.
/// WingId/SquadId are sentinel-bearing scalars (-1 = unassigned), deliberately not FK-constrained.</summary>
public sealed class FleetMemberConfiguration : IEntityTypeConfiguration<FleetMember>
{
    public void Configure(EntityTypeBuilder<FleetMember> builder)
    {
        builder.HasKey(m => m.Id);
        builder.HasIndex(m => m.FleetId);
        builder.HasIndex(m => m.CharacterId);
        // A character is rostered at most once per fleet.
        builder.HasIndex(m => new { m.FleetId, m.CharacterId }).IsUnique();

        // Remote/external members: required bool, CLR-default false (an ordinary session-backed
        // member). Mapped explicitly so the column is non-nullable across every provider.
        builder.Property(m => m.IsExternal).IsRequired();

        // Pilot-reported can-fly verdict: int-stored enum, CLR-default Unknown (0).
        builder.Property(m => m.FitSkillVerdict).IsRequired();

        builder.HasOne<Fleet>()
            .WithMany()
            .HasForeignKey(m => m.FleetId)
            .OnDelete(DeleteBehavior.Cascade);

        // The fit a member flies: an optional owned snapshot, table-split onto the member row. No
        // IsRequired navigation — a member without an assignment leaves the owned columns null.
        builder.OwnsOne(m => m.AssignedFit, FitReferenceMapping.Configure);
    }
}
