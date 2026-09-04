using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Record how this run was looted, or take the answer back with null. Written when the pilot presses the
/// chip rather than at SAVE, so a run the app finishes by itself (ET-179) keeps what was already decided.</summary>
public sealed record SetRunLootStrategyCommand(Guid RunId, RunLootStrategy? LootStrategy) : ICommand<Result>;
