using EveUtils.Shared.Modules.Skills.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// Client-only module: a character's imported effective skill levels. The entity lives in <c>Shared</c> so the
/// migration plumbing can reach the EF model; only the <c>ClientDbContext</c> applies this config.
/// </summary>
public static class SkillsModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new CharacterSkillConfiguration());
        modelBuilder.ApplyConfiguration(new CharacterAttributesConfiguration());        // training attributes
        modelBuilder.ApplyConfiguration(new CharacterSkillQueueEntryConfiguration());    // training queue
    }
}
