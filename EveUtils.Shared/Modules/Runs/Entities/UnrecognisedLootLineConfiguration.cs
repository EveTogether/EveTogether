using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class UnrecognisedLootLineConfiguration : IEntityTypeConfiguration<UnrecognisedLootLine>
{
    public void Configure(EntityTypeBuilder<UnrecognisedLootLine> builder)
    {
        builder.HasKey(line => line.Id);
        builder.Property(line => line.Name).IsRequired().HasMaxLength(255);
        builder.Property(line => line.ResolvedUnitPrice).HasPrecision(18, 2);
        builder.HasIndex(line => line.RunLootCaptureId);
        builder.HasIndex(line => line.Status);
        builder.HasOne(line => line.RunLootCapture)
            .WithMany(capture => capture.UnrecognisedLines)
            .HasForeignKey(line => line.RunLootCaptureId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
