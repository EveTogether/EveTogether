using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunCombatTimelineConfiguration : IEntityTypeConfiguration<RunCombatTimeline>
{
    public void Configure(EntityTypeBuilder<RunCombatTimeline> builder)
    {
        builder.HasKey(timeline => timeline.RunId);
        builder.Property(timeline => timeline.MaxHitOutTarget).HasMaxLength(255);
        builder.Property(timeline => timeline.MaxHitInSource).HasMaxLength(255);
        builder.HasOne<Run>()
            .WithOne()
            .HasForeignKey<RunCombatTimeline>(timeline => timeline.RunId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(timeline => timeline.Series)
            .WithOne()
            .HasForeignKey(series => series.RunId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(timeline => timeline.HitTallies)
            .WithOne()
            .HasForeignKey(tally => tally.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
