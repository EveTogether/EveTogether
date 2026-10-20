using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Forgets the records of <paramref name="ServerAddress"/> (ET-515): the named activities', or with
/// <paramref name="ActivityRefs"/> null all of them — what an opt-out that has withdrawn everything ends with.</summary>
public sealed record DeleteOutcomeShareRecordsCommand(string ServerAddress, IReadOnlyCollection<string>? ActivityRefs = null)
    : ICommand<Result>;
