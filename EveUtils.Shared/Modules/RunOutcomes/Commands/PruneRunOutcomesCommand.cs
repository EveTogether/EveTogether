using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.RunOutcomes.Commands;

/// <summary>Removes the outcomes completed before <paramref name="CompletedBefore"/>, with their loot lines: the
/// retention the server's job runs daily (ET-514). Returns how many.</summary>
public sealed record PruneRunOutcomesCommand(DateOnly CompletedBefore) : ICommand<Result<int>>;
