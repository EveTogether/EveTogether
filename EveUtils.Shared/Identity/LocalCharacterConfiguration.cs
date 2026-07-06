using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Identity;

/// <summary>EF mapping for <see cref="LocalCharacter"/>. Client-only table — applied by the
/// ClientDbContext, so it lands in the client SQLite only. Table name = entity name ("LocalCharacter"),
/// per the project convention (no ToTable — Shared references base EF Core only).</summary>
public sealed class LocalCharacterConfiguration : IEntityTypeConfiguration<LocalCharacter>
{
    public void Configure(EntityTypeBuilder<LocalCharacter> builder)
    {
        builder.HasKey(c => c.EsiCharacterId);
        builder.Property(c => c.EsiCharacterId).ValueGeneratedNever(); // ESI-supplied id, never generated
        builder.Property(c => c.Name).HasMaxLength(255);
        builder.Property(c => c.GrantedScopesJson).HasMaxLength(2000);
        builder.Property(c => c.SortOrder);
        builder.HasIndex(c => c.SortOrder);
    }
}
