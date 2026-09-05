using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// Default <see cref="IMemberFitSpeedEvaluator"/>: parses the assigned fit's raw ESI JSON and runs it through
/// <see cref="IFitStatsProvider"/> at all-level-5 — the same baseline and the same prop-mod-active max velocity the
/// fit detail window shows for this fit, so the overview row never disagrees with the detail window on the same fit.
/// </summary>
public sealed class MemberFitSpeedEvaluator(IFitStatsProvider stats) : IMemberFitSpeedEvaluator, ISingletonService
{
    public async Task<MemberFitSpeedStats?> EvaluateAsync(FitReferenceInfo? assignedFit)
    {
        if (assignedFit is null)
            return null;

        EsiFitting? fit;
        try { fit = JsonSerializer.Deserialize<EsiFitting>(assignedFit.RawJson); }
        catch (JsonException) { fit = null; }
        if (fit is null)
            return null;

        var result = await stats.ComputeAsync(fit);
        return result is null ? null : new MemberFitSpeedStats(result.MaxVelocity, result.WarpSpeed, result.AlignTime);
    }
}
