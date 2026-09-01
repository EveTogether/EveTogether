using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class ActivitySummaryConfiguration : IEntityTypeConfiguration<ActivitySummary>
{
    public void Configure(EntityTypeBuilder<ActivitySummary> builder)
    {
        builder.HasKey(summary => summary.Id);
        builder.Property(summary => summary.GroupCode).HasMaxLength(64);
        builder.Property(summary => summary.SiteName).HasMaxLength(255);
        builder.Property(summary => summary.LootIskGained).HasPrecision(18, 2);
        builder.Property(summary => summary.LootIskLost).HasPrecision(18, 2);
        builder.Property(summary => summary.LootIskNet).HasPrecision(18, 2);
        builder.Property(summary => summary.LootVolume).HasPrecision(18, 3);
        builder.Property(summary => summary.BountyIsk).HasPrecision(18, 2);
        builder.Property(summary => summary.ExpectedPayoutIsk).HasPrecision(18, 2);
        builder.HasIndex(summary => summary.GroupCode).IsUnique();
        builder.HasIndex(summary => summary.RunId).IsUnique();
    }
}
