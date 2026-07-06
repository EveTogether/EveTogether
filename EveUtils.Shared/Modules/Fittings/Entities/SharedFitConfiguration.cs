using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fittings.Entities;

public sealed class SharedFitConfiguration : IEntityTypeConfiguration<SharedFit>
{
    public void Configure(EntityTypeBuilder<SharedFit> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Name).IsRequired().HasMaxLength(255);
        builder.Property(f => f.SharedByCharacterName).IsRequired().HasMaxLength(255);
        builder.Property(f => f.RawJson).IsRequired();
        builder.Property(f => f.ContentHash).IsRequired().HasMaxLength(32);
        builder.HasIndex(f => f.ContentHash); // dedup lookup key (not unique: code enforces dedup + reports the match)
    }
}
