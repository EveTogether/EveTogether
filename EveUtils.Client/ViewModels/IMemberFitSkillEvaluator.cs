using System.Threading.Tasks;
using EveUtils.Client.Fleet;

namespace EveUtils.Client.ViewModels;

/// <summary>Produces the roster can-fly badge for a member: does the character train every skill its
/// assigned fit needs? Returns null when there is no verdict — no assigned fit, the validator/SDE is unavailable, or the
/// character's skills are not locally known (unknown ≠ "can't fly"). Skills are read cache-only, so listing a roster
/// never fires an ESI import.</summary>
public interface IMemberFitSkillEvaluator
{
    Task<MemberSkillBadge?> EvaluateAsync(int characterId, FitReferenceInfo? assignedFit);
}
