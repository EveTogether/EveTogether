using System;
using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// Default <see cref="IMemberFitSkillEvaluator"/>: reads the character's cached trained skills and
/// diffs the assigned fit's required skills against them. Skills are read cache-only (no ESI import on a roster list).
/// The validator is resolved per call so a build without the dogma SDE (no <see cref="IFitValidator"/>) simply yields
/// no verdict instead of failing. A character with no locally known skills also yields no verdict (unknown ≠ "can't fly").
/// </summary>
public sealed class MemberFitSkillEvaluator(ICharacterSkillRepository skills, IServiceProvider services)
    : IMemberFitSkillEvaluator, ISingletonService
{
    public async Task<MemberSkillBadge?> EvaluateAsync(int characterId, FitReferenceInfo? assignedFit)
    {
        var validator = services.GetService<IFitValidator>();
        if (assignedFit is null || validator is null)
            return null;

        var levels = await skills.GetLevelsAsync(characterId);
        if (levels.Count == 0)
            return null;   // skills not locally known for this character → no verdict

        EsiFitting? fit;
        try { fit = JsonSerializer.Deserialize<EsiFitting>(assignedFit.RawJson); }
        catch { fit = null; }
        if (fit is null)
            return null;

        var gaps = validator.ValidateSkills(fit, levels);
        return gaps.Count == 0
            ? new MemberSkillBadge(CanFly: true, "Can fly this fit")
            : new MemberSkillBadge(CanFly: false, $"{gaps.Count} skill{(gaps.Count == 1 ? "" : "s")} missing");
    }
}
