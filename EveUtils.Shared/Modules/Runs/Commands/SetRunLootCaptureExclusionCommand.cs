using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Puts one loot capture back in or takes it out of its run's totals. Never deletes it — a repeat stays on
/// the run either way, so the player can always correct which side of the flag it fell on (ET-65 AC-6).</summary>
public sealed record SetRunLootCaptureExclusionCommand(Guid CaptureId, bool IsExcluded) : ICommand<Result>;
