using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Skills.Plans.Entities;

public sealed class SkillPlanSourceConfiguration : IEntityTypeConfiguration<SkillPlanSource>
{
    public void Configure(EntityTypeBuilder<SkillPlanSource> builder)
    {
        builder.HasKey(source => source.Id);
        builder.Property(source => source.SourceRef).HasMaxLength(255);
        builder.Property(source => source.Label).HasMaxLength(255);
        builder.Property(source => source.DroppedLevels).HasMaxLength(2000);
        builder.HasIndex(source => source.PlanId);
    }
}
