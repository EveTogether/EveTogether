using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunConfiguration : IEntityTypeConfiguration<Run>
{
    public void Configure(EntityTypeBuilder<Run> builder)
    {
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).ValueGeneratedNever();
        builder.Property(run => run.GroupCode).HasMaxLength(64);
        builder.Property(run => run.FormerGroupCode).HasMaxLength(64);
        builder.Property(run => run.SiteName).HasMaxLength(255);
        builder.Property(run => run.Signature).HasMaxLength(128);
        builder.Property(run => run.FitContentHash).HasMaxLength(128);
        builder.Property(run => run.FitNameSnapshot).HasMaxLength(255);
        builder.Property(run => run.SyncServerAddress).HasMaxLength(255);
        builder.HasIndex(run => new { run.GroupCode, run.CharacterId });
    }
}
