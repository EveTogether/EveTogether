using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Messaging.Entities;

/// <summary>EF mapping for <see cref="QueuedMessage"/>. Server-only table; table name = entity name.
/// The (recipient, status) index backs the on-connect delivery sweep; the expiry index backs the retention
/// cleanup.</summary>
public sealed class QueuedMessageConfiguration : IEntityTypeConfiguration<QueuedMessage>
{
    public void Configure(EntityTypeBuilder<QueuedMessage> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Title).IsRequired().HasMaxLength(255);
        builder.Property(m => m.Body).HasMaxLength(4000);
        builder.Property(m => m.PayloadJson).HasMaxLength(16384);

        // Store timestamps as Unix-ms longs: SQLite cannot translate DateTimeOffset comparisons (the retention
        // sweep filters on ExpiresAt) — same constraint as the SavedAtUnixMs choice.
        builder.Property(m => m.CreatedAt).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        builder.Property(m => m.ExpiresAt).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));

        builder.HasIndex(m => new { m.RecipientCharacterId, m.Status });
        builder.HasIndex(m => m.ExpiresAt);
    }
}
