using System.Threading.Tasks;
using EveUtils.Client.Fleet;

namespace EveUtils.Client.ViewModels;

/// <summary>Produces the roster speed figures for a member's assigned fit (ET-40): max velocity, warp speed and
/// align time, read off the same Dogma engine the fit detail window uses. Returns null when there is no assigned
/// fit, its RawJson doesn't parse, or the SDE/engine is unavailable — no figures shown rather than zeroes.</summary>
public interface IMemberFitSpeedEvaluator
{
    Task<MemberFitSpeedStats?> EvaluateAsync(FitReferenceInfo? assignedFit);
}
