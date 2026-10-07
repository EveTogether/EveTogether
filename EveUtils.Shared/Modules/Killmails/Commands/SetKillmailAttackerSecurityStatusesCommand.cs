using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Shared.Modules.Killmails.Commands;

/// <summary>Fills in the security status of stored attackers that were kept without one (ET-477), from the mails the
/// importer re-read from ESI. An attacker that already has a value is left alone.</summary>
public sealed record SetKillmailAttackerSecurityStatusesCommand(
    IReadOnlyDictionary<int, IReadOnlyList<KillmailAttackerSecurityStatus>> StatusesByKillmailId) : ICommand<Result>;
