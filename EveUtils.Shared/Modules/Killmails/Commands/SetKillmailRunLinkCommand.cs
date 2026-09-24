using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Killmails.Commands;

/// <summary>The pilot links a loss to <paramref name="RunId"/>, or unlinks it when null (ET-331). Either way the link
/// becomes <see cref="Entities.KillmailLinkSource.Manual"/>, so the automatic rule never undoes it.</summary>
public sealed record SetKillmailRunLinkCommand(int CharacterId, int KillmailId, Guid? RunId) : ICommand<Result>;
