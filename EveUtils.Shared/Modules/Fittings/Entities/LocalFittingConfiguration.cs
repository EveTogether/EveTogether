using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fittings.Entities;

public sealed class LocalFittingConfiguration : IEntityTypeConfiguration<LocalFitting>
{
    public void Configure(EntityTypeBuilder<LocalFitting> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.OwnerId).IsRequired().HasMaxLength(64);
        builder.Property(f => f.Name).IsRequired().HasMaxLength(255);
        builder.Property(f => f.Description).HasMaxLength(2000);
        builder.Property(f => f.Tags).HasMaxLength(512);
        builder.Property(f => f.RawJson).IsRequired();
        builder.Property(f => f.ContentHash).IsRequired().HasMaxLength(32);
        builder.HasIndex(f => new { f.OwnerId, f.EsiFittingId }).IsUnique();
        builder.HasIndex(f => f.ContentHash); // dedup lookup key (not unique: code enforces dedup + reports the match)
    }
}
