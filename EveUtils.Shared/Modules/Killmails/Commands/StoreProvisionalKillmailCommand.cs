using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Commands;

/// <summary>Stores one provisional killmail parsed from pasted clipboard text, for one own character on the mail
/// (ET-340). One command per character: a mail naming several own characters stores one row each.</summary>
public sealed record StoreProvisionalKillmailCommand(int CharacterId, ProvisionalKillmail Killmail) : ICommand<Result>;
