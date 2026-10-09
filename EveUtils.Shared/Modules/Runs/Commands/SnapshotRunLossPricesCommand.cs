using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Fixes the price of the losses linked to these runs as they are linked now (ET-464): a loss that just joined
/// one is priced at the cache's current figures, one that left it takes its prices along out. Prices already fixed stay.
/// Sent by whoever moves a link, before it rebuilds the activities; never a correction, as a loss is never published.
/// The result is how many runs changed.</summary>
public sealed record SnapshotRunLossPricesCommand(IReadOnlyList<Guid> RunIds) : ICommand<Result<int>>;
