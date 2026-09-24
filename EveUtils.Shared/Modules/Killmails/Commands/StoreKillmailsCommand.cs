using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Commands;

/// <summary>Stores killmails fetched from ESI for one character, skipping any already stored (ET-383). The fetching
/// stays with the importer; this is its only way to write.</summary>
public sealed record StoreKillmailsCommand(int CharacterId, IReadOnlyList<LocalKillmail> Killmails) : ICommand<Result>;
