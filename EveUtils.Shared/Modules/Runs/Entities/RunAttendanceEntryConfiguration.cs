using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunAttendanceEntryConfiguration : IEntityTypeConfiguration<RunAttendanceEntry>
{
    public void Configure(EntityTypeBuilder<RunAttendanceEntry> builder)
    {
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.CharacterName).HasMaxLength(255);
        // One line per character per run (ET-230): a decision replaces the whole list, never adds a second line.
        builder.HasIndex(entry => new { entry.RunId, entry.CharacterId }).IsUnique();
        builder.HasOne(entry => entry.Run)
            .WithMany(run => run.AttendanceEntries)
            .HasForeignKey(entry => entry.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
