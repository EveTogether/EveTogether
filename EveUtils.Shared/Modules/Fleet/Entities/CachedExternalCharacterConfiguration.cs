using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>EF mapping for <see cref="CachedExternalCharacter"/>. Client-only table. The ESI character
/// id is the primary key, so an upsert refreshes the single row for that character on every re-fetch.</summary>
public sealed class CachedExternalCharacterConfiguration : IEntityTypeConfiguration<CachedExternalCharacter>
{
    public void Configure(EntityTypeBuilder<CachedExternalCharacter> builder)
    {
        builder.HasKey(c => c.CharacterId);
        builder.Property(c => c.CharacterId).ValueGeneratedNever(); // the ESI id is supplied, not generated.
        builder.Property(c => c.Name).IsRequired().HasMaxLength(255);
        builder.Property(c => c.Corp).HasMaxLength(255);
        builder.Property(c => c.Alliance).HasMaxLength(255);
    }
}
