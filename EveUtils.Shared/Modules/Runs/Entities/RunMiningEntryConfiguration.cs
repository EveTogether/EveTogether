using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunMiningEntryConfiguration : IEntityTypeConfiguration<RunMiningEntry>
{
    public void Configure(EntityTypeBuilder<RunMiningEntry> builder)
    {
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.OreType).IsRequired();
        // One row per ore per run (ET-229): the upsert the command handler does depends on this being unique.
        builder.HasIndex(entry => new { entry.RunId, entry.OreType }).IsUnique();
        builder.HasOne(entry => entry.Run)
            .WithMany(run => run.MiningEntries)
            .HasForeignKey(entry => entry.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
