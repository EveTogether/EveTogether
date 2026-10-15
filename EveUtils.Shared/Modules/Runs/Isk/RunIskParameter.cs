using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>One reward line a run carries, as far as its ISK goes — a stored <c>RunParameter</c> or the open window's
/// <c>RunParameterInput</c>, read the same way. <paramref name="ItemValue"/> is what an item reward is worth — its
/// count at the unit price — and null while that item has no price yet (ET-509).</summary>
public sealed record RunIskParameter(RunParameterKey Key, decimal? Amount, int? BonusWindowSeconds, DateTime ObservedAtUtc,
    decimal? ItemValue = null);
