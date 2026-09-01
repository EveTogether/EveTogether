using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunLootCaptureConfiguration : IEntityTypeConfiguration<RunLootCapture>
{
    public void Configure(EntityTypeBuilder<RunLootCapture> builder)
    {
        builder.HasKey(capture => capture.Id);
        builder.Property(capture => capture.ContentHash).HasMaxLength(64);
        builder.HasOne(capture => capture.Run)
            .WithMany(run => run.LootCaptures)
            .HasForeignKey(capture => capture.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
