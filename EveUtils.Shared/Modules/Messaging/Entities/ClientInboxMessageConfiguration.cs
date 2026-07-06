using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Messaging.Entities;

/// <summary>EF mapping for <see cref="ClientInboxMessage"/>. Client-only table. The unique
/// (recipient, server-message) index makes re-delivery idempotent — a re-pushed invite updates its row
/// instead of duplicating it.</summary>
public sealed class ClientInboxMessageConfiguration : IEntityTypeConfiguration<ClientInboxMessage>
{
    public void Configure(EntityTypeBuilder<ClientInboxMessage> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.ServerAddress).HasMaxLength(255);
        builder.Property(m => m.Title).IsRequired().HasMaxLength(255);
        builder.Property(m => m.Body).HasMaxLength(4000);
        builder.Property(m => m.PayloadJson).HasMaxLength(16384);

        builder.HasIndex(m => new { m.RecipientCharacterId, m.ServerMessageId }).IsUnique();
    }
}
