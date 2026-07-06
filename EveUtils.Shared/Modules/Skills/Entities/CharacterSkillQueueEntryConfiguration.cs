using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Skills.Entities;

public sealed class CharacterSkillQueueEntryConfiguration : IEntityTypeConfiguration<CharacterSkillQueueEntry>
{
    public void Configure(EntityTypeBuilder<CharacterSkillQueueEntry> builder)
    {
        builder.HasKey(e => new { e.CharacterId, e.QueuePosition });
        builder.Property(e => e.CharacterId).ValueGeneratedNever();
        builder.Property(e => e.QueuePosition).ValueGeneratedNever();
    }
}
