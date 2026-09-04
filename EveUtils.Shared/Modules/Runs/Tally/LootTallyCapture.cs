using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Tally;

/// <summary>One capture as <see cref="LootTally"/> reads it. Oldest first is the order the rule counts on.</summary>
public sealed record LootTallyCapture(LootCaptureRole Role, bool IsExcluded, IReadOnlyList<LootTallyLine> Lines);
