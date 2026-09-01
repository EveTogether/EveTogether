using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// End this pilot's part in a shared run. There is no group entity to delete (ET-104): discard stops the activity
/// and unlinks the group code, keeping the former code as an audit value. It never removes a row, a loot capture or
/// a bounty — a member who already saved keeps their run intact as a standalone one (ET-105 AC-1).
/// </summary>
public sealed record DiscardRunCommand(Guid RunId, DateTime StoppedAtUtc) : ICommand<Result>;
