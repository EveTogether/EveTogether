using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunEnemyObservationConfiguration : IEntityTypeConfiguration<RunEnemyObservation>
{
    public void Configure(EntityTypeBuilder<RunEnemyObservation> builder)
    {
        builder.HasKey(observation => observation.Id);
        builder.Property(observation => observation.EnemyName).IsRequired().HasMaxLength(255);
        builder.HasOne(observation => observation.Run)
            .WithMany(run => run.EnemyObservations)
            .HasForeignKey(observation => observation.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
