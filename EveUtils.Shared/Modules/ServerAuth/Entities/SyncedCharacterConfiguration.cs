using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.ServerAuth.Entities;

public sealed class SyncedCharacterConfiguration : IEntityTypeConfiguration<SyncedCharacter>
{
    public void Configure(EntityTypeBuilder<SyncedCharacter> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.CharacterName).HasMaxLength(255);
        builder.HasIndex(c => c.EsiCharacterId).IsUnique();
    }
}
