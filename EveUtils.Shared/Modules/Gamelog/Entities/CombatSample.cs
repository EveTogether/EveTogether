using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Entities;

/// <summary>
/// A persisted combat hit — internal to the Gamelog module. The first owner-bearing entity
/// (<see cref="IOwnedEntity"/>, foundation pillar 4): every row is stamped with the owner so
/// queries can already scope by it (trivially one owner in v1, per-principal in v2).
/// </summary>
public sealed class CombatSample : IOwnedEntity
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public int? CharacterId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public int Amount { get; set; }
    public DamageDirection Direction { get; set; }
    public string Target { get; set; } = string.Empty;
}
