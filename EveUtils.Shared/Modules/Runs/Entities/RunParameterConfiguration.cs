using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunParameterConfiguration : IEntityTypeConfiguration<RunParameter>
{
    public void Configure(EntityTypeBuilder<RunParameter> builder)
    {
        builder.HasKey(parameter => parameter.Id);
        builder.Property(parameter => parameter.TypedValue).IsRequired().HasMaxLength(255);
        builder.Property(parameter => parameter.Amount).HasPrecision(18, 2);
        builder.HasOne(parameter => parameter.Run)
            .WithMany(run => run.Parameters)
            .HasForeignKey(parameter => parameter.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
