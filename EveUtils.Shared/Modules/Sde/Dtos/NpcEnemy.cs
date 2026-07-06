namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// A searchable NPC entity (category 11) with its resolved group name — used by the damage profile selector
/// to let the player pick a specific NPC type as the incoming damage source.
/// </summary>
public sealed record NpcEnemy(int TypeId, string Name, string GroupName);
