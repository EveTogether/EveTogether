using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Hangs one loot capture on the run that is running now. The result carries the earlier capture's time when
/// this one is a repeat and was therefore stored excluded, and null when it counts towards the totals.</summary>
public sealed record AddRunLootCaptureCommand(RunLootCaptureInput Capture) : ICommand<Result<DateTime?>>;
