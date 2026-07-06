using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>EF mapping for <see cref="FleetInvite"/>. Cascade-deleted with its <see cref="Fleet"/>.
/// The (invitee, status) index backs the on-attach pending-invite sync.</summary>
public sealed class FleetInviteConfiguration : IEntityTypeConfiguration<FleetInvite>
{
    public void Configure(EntityTypeBuilder<FleetInvite> builder)
    {
        builder.HasKey(i => i.Id);
        builder.HasIndex(i => i.FleetId);
        builder.HasIndex(i => new { i.InviteeCharacterId, i.Status });

        builder.Property(i => i.Message).HasMaxLength(1000);

        builder.HasOne<Fleet>()
            .WithMany()
            .HasForeignKey(i => i.FleetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
