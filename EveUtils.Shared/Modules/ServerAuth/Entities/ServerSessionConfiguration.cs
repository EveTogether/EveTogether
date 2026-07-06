using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.ServerAuth.Entities;

public sealed class ServerSessionConfiguration : IEntityTypeConfiguration<ServerSession>
{
    public void Configure(EntityTypeBuilder<ServerSession> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.AccessTokenHash).HasMaxLength(128);
        builder.Property(s => s.RefreshTokenHash).HasMaxLength(128);
        builder.HasIndex(s => s.AccessTokenHash);
        builder.HasIndex(s => s.RefreshTokenHash);
        builder.HasOne(s => s.SyncedCharacter)
            .WithMany()
            .HasForeignKey(s => s.SyncedCharacterId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
