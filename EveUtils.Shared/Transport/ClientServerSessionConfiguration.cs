using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Transport;

/// <summary>EF mapping for <see cref="ClientServerSession"/>. Composite key (Address, CharacterId)
/// so a second character paired to the same server does not overwrite the first. Client-only table.</summary>
public sealed class ClientServerSessionConfiguration : IEntityTypeConfiguration<ClientServerSession>
{
    public void Configure(EntityTypeBuilder<ClientServerSession> builder)
    {
        builder.HasKey(s => new { s.Address, s.CharacterId });
        builder.Property(s => s.Address).HasMaxLength(512);
        builder.Property(s => s.AccessToken).HasMaxLength(512);
        builder.Property(s => s.RefreshToken).HasMaxLength(512);
        builder.Property(s => s.CharacterName).HasMaxLength(255);
        // ServerSessionId needs no mapping of its own: a non-nullable int lands as NOT NULL DEFAULT 0, and 0 is
        // exactly what "the server has not told us yet" means — so every row predating the column reads correctly
        // without a backfill.
    }
}
