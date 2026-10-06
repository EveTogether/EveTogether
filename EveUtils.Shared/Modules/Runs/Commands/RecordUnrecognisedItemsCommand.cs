using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Keeps item names that belong to no run — the appraisal tool's — in the unrecognised log (ET-460). A name
/// already open from the same input is not added twice, so appraising the same list again does not grow the log.</summary>
public sealed record RecordUnrecognisedItemsCommand(IReadOnlyList<UnrecognisedLootNameInput> Names) : ICommand<Result<int>>;
