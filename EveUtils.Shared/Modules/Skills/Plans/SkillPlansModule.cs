using EveUtils.Shared.Modules.Skills.Plans.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Skills.Plans;

/// <summary>
/// Client-only sub-module: a character's local skill plans (ET-355). Kept separate from <c>SkillsModule</c> so this
/// signalling module's write-repository guard does not reach <c>EsiSkillImporter</c> and <c>SkillsWindowViewModel</c>,
/// which still take the plain Skills repositories directly.
/// </summary>
public static class SkillPlansModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SkillPlanConfiguration());
        modelBuilder.ApplyConfiguration(new SkillPlanRowConfiguration());
    }
}
