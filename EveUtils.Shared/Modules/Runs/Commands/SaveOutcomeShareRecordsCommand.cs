using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Writes down where this client stands with the named outcomes on their servers (ET-515): each record
/// replaces the one for the same server and activity.</summary>
public sealed record SaveOutcomeShareRecordsCommand(IReadOnlyList<OutcomeShareRecordDto> Records) : ICommand<Result>;
