using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>Validates a fit for the selected character: missing skills and resource overloads.</summary>
public interface IFitValidator
{
    /// <summary>Validates the fit against its computed <paramref name="stats"/> and the character's trained skills.
    /// Pass <paramref name="trainedSkills"/> null for an all-skills baseline (no skill gaps reported).</summary>
    FitValidationResult Validate(EsiFitting fit, FitStats stats, IReadOnlyDictionary<int, int>? trainedSkills);

    /// <summary>The skill gaps only — the skills the fit needs that <paramref name="trainedSkills"/> lacks — without
    /// the dogma stats compute the resource-overload check needs. The roster can-fly badge uses this:
    /// it only asks "can this pilot fly the assigned fit", so paying for a full stats recompute per member is wasted.</summary>
    IReadOnlyList<SkillGap> ValidateSkills(EsiFitting fit, IReadOnlyDictionary<int, int> trainedSkills);
}
