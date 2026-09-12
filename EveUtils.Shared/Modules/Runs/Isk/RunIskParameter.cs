using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>One reward line a run carries, as far as its ISK goes — a stored <c>RunParameter</c> or the open window's
/// <c>RunParameterInput</c>, read the same way.</summary>
public sealed record RunIskParameter(RunParameterKey Key, decimal? Amount, int? BonusWindowSeconds, DateTime ObservedAtUtc);
