using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Skills.Plans.Entities;

public sealed class SkillPlanRowConfiguration : IEntityTypeConfiguration<SkillPlanRow>
{
    public void Configure(EntityTypeBuilder<SkillPlanRow> builder)
    {
        builder.HasKey(row => row.Id);
        builder.Property(row => row.SourceRef).HasMaxLength(255);
        builder.Property(row => row.SourceLabel).HasMaxLength(255);
        builder.HasIndex(row => new { row.PlanId, row.Position });
        // Dedupe key: AddSkillPlanRowsCommand never stores a second row for a (skill, level) already in the plan.
        builder.HasIndex(row => new { row.PlanId, row.SkillTypeId, row.Level }).IsUnique();
    }
}
