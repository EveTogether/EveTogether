namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>One enemy type the sharing pilot saw in one room of their run (ET-498), with the count they typed for it — 0
/// is "seen, not counted", as in their own ENEMIES list. <see cref="TypeId"/> is null for a name their SDE has no type
/// for; <see cref="Room"/> is null while their run has no rooms.</summary>
public sealed record RunShareEnemyLine(string Name, int? TypeId, int? Room, int Count);
