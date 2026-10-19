using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.RunOutcomes.Entities;

public sealed class RunOutcomeConfiguration : IEntityTypeConfiguration<RunOutcome>
{
    public void Configure(EntityTypeBuilder<RunOutcome> builder)
    {
        builder.HasKey(outcome => outcome.Id);
        builder.Property(outcome => outcome.ActivityId).IsRequired().HasMaxLength(64);
        builder.Property(outcome => outcome.ContributorBucket).IsRequired().HasMaxLength(64);
        builder.Property(outcome => outcome.KeyCode).IsRequired().HasMaxLength(320);
        builder.Property(outcome => outcome.NormalizedName).HasMaxLength(255);
        builder.Property(outcome => outcome.AbyssalWeather).HasMaxLength(32);
        builder.Property(outcome => outcome.FixedIsk).HasPrecision(18, 2);
        // One outcome per activity: the fleet mates who flew it together share one ActivityId.
        builder.HasIndex(outcome => outcome.ActivityId).IsUnique();
        builder.HasIndex(outcome => new { outcome.KeyCode, outcome.CompletedOn });
        builder.HasIndex(outcome => outcome.ContributorBucket);
        builder.HasIndex(outcome => outcome.CompletedOn);
    }
}
