using EveUtils.Shared.Modules.Gamelog.Aggregation;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// How well a character's main weapon lands on its current target. <c>Verdict</c> is camelCase text — <c>idle</c>,
/// <c>notEnoughShots</c>, <c>notMeasurable</c>, <c>adjust</c>, <c>ok</c>, <c>sweetSpot</c>, <c>learning</c> — so a
/// widget does not depend on enum numbers. <c>Percent</c> is 0–100 and only set for <c>adjust</c>, <c>ok</c> and
/// <c>sweetSpot</c>; <c>Breakdown</c> names each weapon's own figure.
/// </summary>
public sealed record ApplicationDto(string Verdict, double? Percent, string? Breakdown)
{
    public static ApplicationDto FromSummary(ApplicationSummary summary)
    {
        var name = summary.Verdict.ToString();
        return new ApplicationDto(char.ToLowerInvariant(name[0]) + name[1..], summary.Percent, summary.Breakdown);
    }
}
