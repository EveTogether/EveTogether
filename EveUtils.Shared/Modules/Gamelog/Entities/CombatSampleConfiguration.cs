using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Gamelog.Entities;

public sealed class CombatSampleConfiguration : IEntityTypeConfiguration<CombatSample>
{
    public void Configure(EntityTypeBuilder<CombatSample> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.OwnerId).HasMaxLength(64);
        builder.Property(s => s.Target).HasMaxLength(255);
        builder.HasIndex(s => new { s.OwnerId, s.Timestamp });
    }
}
