using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Killmails.Commands;

/// <summary>Removes a character's provisional killmail rows matched against a real mail that just landed — time
/// (to the second), ship and victim name (ET-340). A no-op match publishes no signal.</summary>
public sealed record RemoveMatchingProvisionalKillmailCommand(
    int CharacterId, DateTime KillmailTimeUtc, int VictimShipTypeId, string VictimName) : ICommand<Result>;
