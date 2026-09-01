using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Backup.Entities;

/// <summary>EF mapping for <see cref="BackupDownload"/>. Server-only table — table name = entity name
/// ("BackupDownload"), per convention (no ToTable — Shared references base EF Core only).</summary>
public sealed class BackupDownloadConfiguration : IEntityTypeConfiguration<BackupDownload>
{
    public void Configure(EntityTypeBuilder<BackupDownload> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.AdminUsername).IsRequired().HasMaxLength(64);
        builder.Property(d => d.AppVersion).IsRequired().HasMaxLength(32);
        builder.Property(d => d.FileName).IsRequired().HasMaxLength(128);
    }
}
