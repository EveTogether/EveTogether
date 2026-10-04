using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Corrects how many units of one ore a run mined (ET-424), on a running run and on a saved one alike — the
/// mining counterpart of <see cref="SetRunLootManualCommand"/>. Crit never exceeds the new figure, since it is already
/// counted inside it; residue was never part of it and stays. A line is taken away with
/// <see cref="RemoveRunMiningEntryCommand"/>, never by setting zero.</summary>
public sealed record SetRunMiningEntryUnitsCommand(Guid RunId, string OreType, int Units) : ICommand<Result>;
