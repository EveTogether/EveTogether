using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunCombatSeriesConfiguration : IEntityTypeConfiguration<RunCombatSeries>
{
    public void Configure(EntityTypeBuilder<RunCombatSeries> builder)
    {
        builder.HasKey(series => series.Id);
        builder.Property(series => series.Samples).IsRequired();
    }
}
