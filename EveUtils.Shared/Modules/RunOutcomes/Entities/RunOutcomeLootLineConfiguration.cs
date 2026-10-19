using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.RunOutcomes.Entities;

public sealed class RunOutcomeLootLineConfiguration : IEntityTypeConfiguration<RunOutcomeLootLine>
{
    public void Configure(EntityTypeBuilder<RunOutcomeLootLine> builder)
    {
        builder.HasKey(line => line.Id);
        builder.HasOne(line => line.RunOutcome)
            .WithMany(outcome => outcome.LootLines)
            .HasForeignKey(line => line.RunOutcomeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
