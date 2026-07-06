using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>EF mapping for <see cref="FleetJoinRequest"/>. Cascade-deleted with its <see cref="Fleet"/>.
/// The (fleet, status) index backs the owner's pending-request listing; the (requester, status) index backs the
/// duplicate-request guard. Timestamps are stored as Unix-ms longs — SQLite cannot translate DateTimeOffset
/// comparisons, same choice as the queued-message rows.</summary>
public sealed class FleetJoinRequestConfiguration : IEntityTypeConfiguration<FleetJoinRequest>
{
    public void Configure(EntityTypeBuilder<FleetJoinRequest> builder)
    {
        builder.HasKey(r => r.Id);
        builder.HasIndex(r => new { r.FleetId, r.Status });
        builder.HasIndex(r => new { r.RequesterCharacterId, r.Status });

        builder.Property(r => r.CreatedAt).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        builder.Property(r => r.RespondedAt).HasConversion(
            v => v == null ? (long?)null : v.Value.ToUnixTimeMilliseconds(),
            v => v == null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(v.Value));

        builder.HasOne<Fleet>()
            .WithMany()
            .HasForeignKey(r => r.FleetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
