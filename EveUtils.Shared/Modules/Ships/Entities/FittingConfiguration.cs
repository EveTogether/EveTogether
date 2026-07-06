using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Ships.Entities;

public sealed class FittingConfiguration : IEntityTypeConfiguration<Fitting>
{
    public void Configure(EntityTypeBuilder<Fitting> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Name).HasMaxLength(255);
    }
}
