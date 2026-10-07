using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The item names no SDE type carried when they were entered (ET-460), longest waiting first. By default the
/// ones still open — what has to be priced — and with <see cref="UnrecognisedItemStatus.Resolved"/> the record of what
/// was recognised since. Lines of a capture that is left out of the totals do not count.</summary>
public sealed record GetUnrecognisedLootQuery(UnrecognisedItemStatus Status = UnrecognisedItemStatus.Open)
    : IQuery<Result<IReadOnlyList<UnrecognisedLootItemDto>>>;
