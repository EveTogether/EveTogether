using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.ServerAuth.Entities;

public sealed class AllowedCharacterConfiguration : IEntityTypeConfiguration<AllowedCharacter>
{
    public void Configure(EntityTypeBuilder<AllowedCharacter> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.CharacterName).HasMaxLength(255);
        builder.Property(a => a.Note).HasMaxLength(255);
    }
}
