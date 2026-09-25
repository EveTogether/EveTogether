using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Sets a fit-entry's per-fit minimum. Null clears it. <see cref="SkillMinimums"/> replaces the doctrine
/// skill minimums as a whole; null leaves them unchanged (an older client that does not send them). Gated on
/// owner-or-manage. Changing which fit an entry holds is a remove + add.</summary>
public sealed record EditFleetCompositionEntryCommand(
    long EntryId,
    int? EntryMinCount,
    int ActingCharacterId,
    IReadOnlyList<SkillMinimum>? SkillMinimums = null) : ICommand<Result>;
