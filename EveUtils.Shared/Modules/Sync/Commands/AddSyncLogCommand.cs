using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Sync.Commands;

public sealed record AddSyncLogCommand(string EntityName, DateTimeOffset SyncedAtUtc, string? Note) : ICommand<Result<int>>;
