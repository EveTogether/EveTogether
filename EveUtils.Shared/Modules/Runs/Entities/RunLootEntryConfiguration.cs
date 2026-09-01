using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunLootEntryConfiguration : IEntityTypeConfiguration<RunLootEntry>
{
    public void Configure(EntityTypeBuilder<RunLootEntry> builder)
    {
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Name).IsRequired().HasMaxLength(255);
        builder.Property(entry => entry.Volume).HasPrecision(18, 3);
        builder.Property(entry => entry.ClipboardPrice).HasPrecision(18, 2);
        builder.HasOne(entry => entry.RunLootCapture)
            .WithMany(capture => capture.Entries)
            .HasForeignKey(entry => entry.RunLootCaptureId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
