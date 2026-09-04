using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Says what one capture is to its run. The only way a role is handed out, which is what makes a second
/// starting cargo hold impossible rather than something to catch: whoever held the role before this one gives it up
/// in the same write.</summary>
public sealed record SetRunLootCaptureRoleCommand(Guid CaptureId, LootCaptureRole Role) : ICommand<Result>;
