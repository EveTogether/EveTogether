using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Takes one ore's line off a run (ET-424) — its crit and residue go with it, since both are only parts of
/// that line. Running and saved runs alike; see <see cref="SetRunMiningEntryUnitsCommand"/>.</summary>
public sealed record RemoveRunMiningEntryCommand(Guid RunId, string OreType) : ICommand<Result>;
