using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Skills.Entities;

public sealed class CharacterSkillConfiguration : IEntityTypeConfiguration<CharacterSkill>
{
    public void Configure(EntityTypeBuilder<CharacterSkill> builder)
    {
        builder.HasKey(s => new { s.CharacterId, s.SkillTypeId });
        builder.Property(s => s.CharacterId).ValueGeneratedNever();
        builder.Property(s => s.SkillTypeId).ValueGeneratedNever();
    }
}
