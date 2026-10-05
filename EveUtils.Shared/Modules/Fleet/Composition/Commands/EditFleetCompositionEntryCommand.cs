using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Sets a fit-entry's per-fit minimum (null clears it) and, when <see cref="SkillMinimums"/> is not null, replaces the doctrine
/// skill minimums as a whole. Gated on owner-or-manage; changing which fit an entry holds is a remove + add.</summary>
public sealed record EditFleetCompositionEntryCommand(
    long EntryId,
    int? EntryMinCount,
    int ActingCharacterId,
    IReadOnlyList<SkillMinimum>? SkillMinimums = null) : ICommand<Result>;
