using EveUtils.Shared.Cqrs;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>True if the character is on the fleet's roster.</summary>
public sealed record IsFleetMemberQuery(long FleetId, int CharacterId) : IQuery<bool>;
