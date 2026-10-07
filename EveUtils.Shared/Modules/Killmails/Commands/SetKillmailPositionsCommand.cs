using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Shared.Modules.Killmails.Commands;

/// <summary>Fills in the victim position of stored killmails that were kept without one (ET-473), from the mails the
/// importer re-read from ESI. A row that already has a position is left alone.</summary>
public sealed record SetKillmailPositionsCommand(IReadOnlyDictionary<int, KillmailPosition> PositionsByKillmailId) : ICommand<Result>;
