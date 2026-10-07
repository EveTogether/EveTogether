using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Gives every loot, ore and filament line that came in without a price the first one the cache has (ET-463),
/// once: a line priced here never moves again. Only this machine's own runs — a fleetmate's copy is theirs to price, and
/// a correction here could not be published back. A saved run that gains a price is marked corrected, so a published
/// one reads "changed since published". The result is how many runs gained a price.</summary>
public sealed record FillRunPriceSnapshotsCommand : ICommand<Result<int>>;
