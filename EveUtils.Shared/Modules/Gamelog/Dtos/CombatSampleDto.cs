using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Dtos;

/// <summary>A persisted combat hit projected to the outside (UI/query result).</summary>
public sealed record CombatSampleDto(int Id, int? CharacterId, int Amount, DamageDirection Direction, string Target, DateTimeOffset At);
