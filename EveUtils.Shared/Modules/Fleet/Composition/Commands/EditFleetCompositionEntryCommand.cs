using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Sets a fit-entry's per-fit minimum. Null clears it. Gated on
/// owner-or-manage. Changing which fit an entry holds is a remove + add.</summary>
public sealed record EditFleetCompositionEntryCommand(
    long EntryId,
    int? EntryMinCount,
    int ActingCharacterId) : ICommand<Result>;
