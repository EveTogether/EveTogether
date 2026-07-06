using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Skills.Entities;

public sealed class CharacterAttributesConfiguration : IEntityTypeConfiguration<CharacterAttributes>
{
    public void Configure(EntityTypeBuilder<CharacterAttributes> builder)
    {
        builder.HasKey(a => a.CharacterId);
        builder.Property(a => a.CharacterId).ValueGeneratedNever();
    }
}
