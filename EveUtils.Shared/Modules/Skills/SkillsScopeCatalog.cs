using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// ESI scope declarations for the Skills module. The skills endpoint is a snapshot that lags behind the last
/// in-game session, so the skill-queue is read alongside it to count training already finished — both scopes required.
/// Registered via <c>services.AddModuleEsiScopes(SkillsScopeCatalog.Catalog)</c>.
/// </summary>
public static class SkillsScopeCatalog
{
    public const string ReadSkills = "esi-skills.read_skills.v1";
    public const string ReadSkillQueue = "esi-skills.read_skillqueue.v1";

    public static IEsiScopeCatalog Catalog { get; } = new SkillsEsiScopeCatalogImpl();

    private sealed class SkillsEsiScopeCatalogImpl : IEsiScopeCatalog
    {
        public IReadOnlyList<EsiScopeRequirement> Requirements { get; } =
        [
            new EsiScopeRequirement(ReadSkills, EsiScopeTarget.Client, "Skills",
                "Required to compute fit stats with your character's actual trained skills."),
            new EsiScopeRequirement(ReadSkillQueue, EsiScopeTarget.Client, "Skills",
                "Read alongside skills so training finished since the last snapshot counts as trained."),
        ];
    }
}
