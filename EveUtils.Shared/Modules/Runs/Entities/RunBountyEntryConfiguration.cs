using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunBountyEntryConfiguration : IEntityTypeConfiguration<RunBountyEntry>
{
    public void Configure(EntityTypeBuilder<RunBountyEntry> builder)
    {
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Isk).HasPrecision(18, 2);
        builder.HasOne(entry => entry.Run)
            .WithMany(run => run.BountyEntries)
            .HasForeignKey(entry => entry.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
