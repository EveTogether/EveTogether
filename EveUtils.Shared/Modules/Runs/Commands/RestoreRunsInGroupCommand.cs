using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Undoes <see cref="DeleteRunsInGroupCommand"/> for a whole activity — same reasoning as
/// <see cref="RestoreRunCommand"/>, for the grouped case. A group code is minted once per activity and never
/// reused (ET-130/ET-210), so matching on it alone is unambiguous.</summary>
public sealed record RestoreRunsInGroupCommand(string GroupCode) : ICommand<Result<int>>;
