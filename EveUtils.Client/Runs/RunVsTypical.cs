using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.Runs;

/// <summary>A run held against the typical run of its activity (ET-517): the one rule behind the drawer's "Compared to
/// typical" line and the chip on the RUNS list's row, so the two cannot disagree about who is in the top quarter.</summary>
public sealed record RunVsTypical(decimal Typical, decimal Difference, RunBand Band)
{
    /// <summary>Null where the figures have no typical run: below the minimum there is nothing to compare with.</summary>
    public static RunVsTypical? Of(decimal yield, EarningsFiguresDto figures)
    {
        if (figures.Typical is not { } typical)
            return null;

        RunBand band = yield >= (figures.P75 ?? typical) && yield > typical ? RunBand.Top
            : yield <= (figures.P25 ?? typical) && yield < typical ? RunBand.Bottom
            : RunBand.Middle;
        return new RunVsTypical(typical, yield - typical, band);
    }
}
