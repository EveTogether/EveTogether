using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunGroupOriginConfiguration : IEntityTypeConfiguration<RunGroupOrigin>
{
    public void Configure(EntityTypeBuilder<RunGroupOrigin> builder)
    {
        builder.HasKey(origin => origin.GroupCode);
        builder.Property(origin => origin.GroupCode).HasMaxLength(64);
    }
}
