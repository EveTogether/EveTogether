using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Sync.Entities;

public sealed class SyncLogConfiguration : IEntityTypeConfiguration<SyncLog>
{
    public void Configure(EntityTypeBuilder<SyncLog> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.EntityName).HasMaxLength(255);
        builder.Property(l => l.Note).HasMaxLength(2000);
    }
}
