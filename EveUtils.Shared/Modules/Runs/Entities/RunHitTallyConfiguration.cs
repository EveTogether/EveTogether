using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunHitTallyConfiguration : IEntityTypeConfiguration<RunHitTally>
{
    public void Configure(EntityTypeBuilder<RunHitTally> builder)
    {
        builder.HasKey(tally => tally.Id);
        builder.Property(tally => tally.Counterparty).IsRequired().HasMaxLength(255);
        builder.Property(tally => tally.Weapon).HasMaxLength(255);
    }
}
