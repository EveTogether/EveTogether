using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Skills.Plans.Entities;

public sealed class SkillPlanConfiguration : IEntityTypeConfiguration<SkillPlan>
{
    public void Configure(EntityTypeBuilder<SkillPlan> builder)
    {
        builder.HasKey(plan => plan.Id);
        builder.Property(plan => plan.Name).IsRequired().HasMaxLength(255);
        builder.HasIndex(plan => plan.CharacterId);
    }
}
