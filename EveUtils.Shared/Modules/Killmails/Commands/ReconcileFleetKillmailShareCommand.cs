using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Commands;

/// <summary>Brings a fleet mate's rows from one fleet in line with their latest full share (ET-371): stores the newly
/// <paramref name="Fetched"/> mails and removes that fleet's rows no longer in <paramref name="SharedKillmailIds"/>.
/// Own mails and other fleets' rows stay. The fetching stays with the importer; this is its only way to write.</summary>
public sealed record ReconcileFleetKillmailShareCommand(
    int CharacterId, long FleetId, IReadOnlyList<int> SharedKillmailIds, IReadOnlyList<LocalKillmail> Fetched)
    : ICommand<Result>;
