using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Changes the site, destination system and deadline of an escalation a saved run already carries (ET-457),
/// in place under the same entry — its outcome and the run that flew it stay. Registering a second one is
/// <see cref="RegisterRunEscalationCommand"/>.</summary>
public sealed record ChangeRunEscalationCommand(
    Guid RunId, Guid? EntryId, string SiteName, int? DungeonId, string DestinationSystem, int? DestinationSolarSystemId,
    DateTime ExpiresAtUtc) : ICommand<Result>;
