using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>What one source added to a run's TOTAL ISK, or took from it: <paramref name="Amount"/> is signed.</summary>
public sealed record IskContribution(IskSource Source, decimal Amount, IskCertainty Certainty);
