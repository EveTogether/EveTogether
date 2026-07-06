using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Gamelog.Entities;

public sealed class CharacterMetricStateConfiguration : IEntityTypeConfiguration<CharacterMetricState>
{
    public void Configure(EntityTypeBuilder<CharacterMetricState> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.CharacterName).HasMaxLength(128);
        builder.HasIndex(s => s.CharacterName).IsUnique();
        builder.Property(s => s.MinedJson).HasMaxLength(4000);
    }
}
