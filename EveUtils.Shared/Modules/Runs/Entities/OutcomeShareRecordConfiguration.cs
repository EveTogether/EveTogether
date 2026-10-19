using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class OutcomeShareRecordConfiguration : IEntityTypeConfiguration<OutcomeShareRecord>
{
    public void Configure(EntityTypeBuilder<OutcomeShareRecord> builder)
    {
        builder.HasKey(record => new { record.ServerAddress, record.ActivityRef });
        builder.Property(record => record.ServerAddress).HasMaxLength(512);
        builder.Property(record => record.ActivityRef).HasMaxLength(64);
        builder.Property(record => record.LastError).HasMaxLength(512);
        builder.HasIndex(record => record.ActivityRef);
    }
}
