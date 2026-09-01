using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Hangs one loot capture on the run that is running now. See <see cref="RunLootCaptureSaveResult"/> for
/// what the result carries.</summary>
public sealed record AddRunLootCaptureCommand(RunLootCaptureInput Capture) : ICommand<Result<RunLootCaptureSaveResult>>;
